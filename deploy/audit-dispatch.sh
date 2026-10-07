#!/usr/bin/env bash
# Audit-dispatch script — invoked by Workstream's AuditDispatchWorker once per queued
# audit (the request_audit MCP tool enqueues one row per project). It checks out the
# project's repo, equips the audit user, and launches the project's audit orchestrator
# inside a DETACHED tmux session that can be attached to and steered:
#
#   docker compose -f deploy/docker-compose.yml exec dispatcher tmux attach -t audit-<slug>
#
# The script is baked into the dispatcher image at /app/audit-dispatch.sh and runs as
# root inside the dispatcher container (git, tmux, Claude Code, ssh-mcp installed). The
# `claude` run itself is dropped to the non-root `auditor` user.
#
# Environment provided by the worker:
#   WS_ACTION           'run' (launch the orchestrator) or 'cancel' (stop a running one)
#   WS_PROJECT_ID       project uuid
#   WS_PROJECT_SLUG     project slug (local checkout dir name + tmux session suffix)
#   WS_REPO_OWNER       GitHub owner of the project's primary repo
#   WS_REPO_NAME        GitHub repo name
#   WS_REFERENCE_REPOS  space-separated owner/name list of read-only reference repos
#   WS_MCP_TOKEN        the triggering user's Workstream MCP token (credential)
#   WS_REPOS_DIR        base dir for checkouts (default /srv/audit-repos)
#   WS_GIT_TOKEN_FILE   GitHub token file for private clones (default /run/secrets/gh_clone_token)
#   WS_SSH_MCP_HOST     host for the optional ssh-host MCP (unset = not registered)
#   WS_SSH_MCP_USER     SSH user for the ssh-host MCP (default deploy)
#   WS_SSH_MCP_PORT     SSH port for the ssh-host MCP (default 22)
#   WS_SSH_KEY_FILE     SSH key file for the ssh-host MCP (default /run/secrets/ssh_mcp_key)
#   WS_SSH_PASS_FILE    SSH key passphrase file (default /run/secrets/ssh_mcp_passphrase)
#
# A non-zero exit tells the worker the dispatch failed; it retries with backoff.

set -euo pipefail

REPOS_DIR="${WS_REPOS_DIR:-/srv/audit-repos}"
slug="${WS_PROJECT_SLUG:?missing WS_PROJECT_SLUG}"
repo_dir="${REPOS_DIR}/${slug}"
session="audit-${slug}"

echo "[audit-dispatch] action=${WS_ACTION:-run} project=${slug} repo=${WS_REPO_OWNER:-?}/${WS_REPO_NAME:-?}"

# Cancel: signal the running audit to stop. The orchestrator owns the cleanup via its own
# /audit-cancel command (releasing claims, archiving the plan, discarding worktrees).
# Sequence: interrupt (Esc, twice) so /audit-cancel runs immediately instead of queueing
# behind an in-flight subagent; wait for the orchestrator to settle; then /clear the
# conversation and kill the tmux session so nothing lingers.
if [[ "${WS_ACTION:-run}" == "cancel" ]]; then
  if ! tmux has-session -t "${session}" 2>/dev/null; then
    echo "[audit-dispatch] no running audit session ${session}; nothing to cancel"
    exit 0
  fi

  tmux send-keys -t "${session}" Escape
  sleep 3
  tmux send-keys -t "${session}" Escape
  sleep 4
  tmux send-keys -t "${session}" "/audit-cancel" Enter
  echo "[audit-dispatch] interrupted session ${session} and sent /audit-cancel"

  # Wait for the orchestrator to finish its cleanup before tearing down. Claude's busy
  # indicator "esc to interrupt" disappears from the status line when it is idle; we treat
  # two consecutive idle samples (10s) as "settled". Cap at 5 minutes so a never-ending
  # cleanup doesn't block the dispatch worker.
  idle_streak=0
  for _ in $(seq 1 60); do
    pane="$(tmux capture-pane -t "${session}" -p 2>/dev/null || true)"
    if grep -q "esc to interrupt" <<<"${pane}"; then
      idle_streak=0
    else
      idle_streak=$((idle_streak + 1))
      [[ ${idle_streak} -ge 2 ]] && break
    fi
    sleep 5
  done

  # Clear the conversation, then close the session.
  tmux send-keys -t "${session}" "/clear" Enter
  sleep 3
  tmux kill-session -t "${session}" 2>/dev/null || true
  echo "[audit-dispatch] cleared and closed session ${session}"
  exit 0
fi

# A session for this project is already live — leave it; attach to watch it.
if tmux has-session -t "${session}" 2>/dev/null; then
  echo "[audit-dispatch] tmux session ${session} already running; skipping"
  exit 0
fi

# Private repos: a GitHub token (Docker secret) is injected into git for clone/pull only,
# via `-c url.insteadOf`, so it never lands in .git/config or the repos volume.
GIT_TOKEN_FILE="${WS_GIT_TOKEN_FILE:-/run/secrets/gh_clone_token}"
git_auth=()
if [[ -s "${GIT_TOKEN_FILE}" ]]; then
  _tok="$(tr -d '\r\n' < "${GIT_TOKEN_FILE}")"
  git_auth=(-c "url.https://x-access-token:${_tok}@github.com/.insteadOf=https://github.com/")
fi

# clone_or_pull <owner/name> <dest-dir> — clone on first run, else fast-forward.
clone_or_pull() {
  local slug_ref="$1" dest="$2"
  if [[ ! -d "${dest}/.git" ]]; then
    echo "[audit-dispatch] cloning ${slug_ref}"
    git "${git_auth[@]}" clone "https://github.com/${slug_ref}.git" "${dest}"
  else
    git "${git_auth[@]}" -C "${dest}" pull --ff-only \
      || echo "[audit-dispatch] pull skipped for ${slug_ref} (non-ff or offline)"
  fi
  chown -R auditor:auditor "${dest}"
}

# Primary repo.
if [[ -z "${WS_REPO_OWNER:-}" || -z "${WS_REPO_NAME:-}" ]]; then
  echo "[audit-dispatch] no repo registered for ${slug}; add one with add_project_repo" >&2
  exit 1
fi
clone_or_pull "${WS_REPO_OWNER}/${WS_REPO_NAME}" "${repo_dir}"

# Reference repos (read-only design references the orchestrator wants alongside) — each
# is checked out at ${REPOS_DIR}/<repo-name>.
for ref in ${WS_REFERENCE_REPOS:-}; do
  clone_or_pull "${ref}" "${REPOS_DIR}/${ref##*/}"
done

# Hand the Claude config dir to the audit user (the volume ships root-owned).
chown -R auditor:auditor /home/auditor

# In-session `git fetch origin` on a private repo needs a standing credential — the
# clone above used an ephemeral -c the checkout does not retain. Store one for `auditor`.
if [[ -s "${GIT_TOKEN_FILE}" ]]; then
  printf 'https://x-access-token:%s@github.com\n' "$(tr -d '\r\n' < "${GIT_TOKEN_FILE}")" \
    > /home/auditor/.git-credentials
  chmod 600 /home/auditor/.git-credentials
  chown auditor:auditor /home/auditor/.git-credentials
  runuser -u auditor -- git config --global credential.helper store
fi

# Pre-seed the auditor's ~/.claude.json: skip the first-run theme/onboarding wizard, and
# pre-accept the folder-trust dialog for this repo.
node -e '
  const fs = require("fs"), path = "/home/auditor/.claude.json";
  let c = {}; try { c = JSON.parse(fs.readFileSync(path, "utf8")); } catch {}
  c.theme = c.theme || "dark";
  c.hasCompletedOnboarding = true;
  c.projects = c.projects || {};
  c.projects[process.argv[1]] = { ...(c.projects[process.argv[1]] || {}), hasTrustDialogAccepted: true };
  fs.writeFileSync(path, JSON.stringify(c, null, 2));
' "${repo_dir}"
chown auditor:auditor /home/auditor/.claude.json

# register_mcp <name> <add-args…> — (re)register an MCP server in the auditor's user scope.
register_mcp() {
  runuser -u auditor -- env HOME=/home/auditor claude mcp remove --scope user "$1" >/dev/null 2>&1 || true
  runuser -u auditor -- env HOME=/home/auditor claude mcp add --scope user "$@"
}

# workstream MCP — HTTP, over the internal Docker network, as the triggering user: the
# orchestrator's create_plan / create_tasks calls are attributed to them. WS_MCP_TOKEN
# is a credential and is never echoed.
if [[ -n "${WS_MCP_TOKEN:-}" ]]; then
  register_mcp workstream --transport http "http://api:8080/${WS_MCP_TOKEN}/mcp" \
    && echo "[audit-dispatch] registered workstream MCP"
else
  echo "[audit-dispatch] WARNING: no MCP token for the requesting user — /audit-run cannot reach Workstream" >&2
fi

# ssh-host MCP (optional) — stdio ssh-mcp to a test host, for the orchestrator's
# test/verify step. Registered only when WS_SSH_MCP_HOST is set and an SSH key exists.
# The SSH key (Docker secret) is copied to the auditor's ~/.ssh with 0600 perms.
SSH_KEY_SRC="${WS_SSH_KEY_FILE:-/run/secrets/ssh_mcp_key}"
SSH_PASS_FILE="${WS_SSH_PASS_FILE:-/run/secrets/ssh_mcp_passphrase}"
if [[ -n "${WS_SSH_MCP_HOST:-}" && -s "${SSH_KEY_SRC}" ]]; then
  install -d -m 700 -o auditor -g auditor /home/auditor/.ssh
  ssh_key=/home/auditor/.ssh/ssh_mcp_key
  cp "${SSH_KEY_SRC}" "${ssh_key}"
  chmod 600 "${ssh_key}"
  chown auditor:auditor "${ssh_key}"
  ssh_pass=""
  [[ -s "${SSH_PASS_FILE}" ]] && ssh_pass="$(tr -d '\r\n' < "${SSH_PASS_FILE}")"
  register_mcp ssh-host -- npx ssh-mcp -y -- \
    --host="${WS_SSH_MCP_HOST}" --port="${WS_SSH_MCP_PORT:-22}" --user="${WS_SSH_MCP_USER:-deploy}" \
    --key="${ssh_key}" --keyPassphrase="${ssh_pass}" \
    --timeout=300000 --maxChars=none \
    && echo "[audit-dispatch] registered ssh-host MCP"
else
  echo "[audit-dispatch] WS_SSH_MCP_HOST unset or no SSH key at ${SSH_KEY_SRC}; skipping ssh-host MCP"
fi

# Launch the project's audit orchestrator as `auditor` in a detached, attachable tmux
# session. --dangerously-skip-permissions bypasses the per-tool approval prompts so the
# run is unattended (the project's own /audit-run, on first-party code).
tmux new-session -d -s "${session}" -c "${repo_dir}" \
  runuser -u auditor -- env HOME=/home/auditor claude --dangerously-skip-permissions "/audit-run"

# Two startup gates that neither --dangerously-skip-permissions nor config keys reliably
# suppress, answered here so the run proceeds unattended:
#   1. "trust this folder?"        — "Yes" is pre-selected, so Enter confirms.
#   2. "Bypass Permissions mode"   — "No, exit" is pre-selected (!), so arrow Down first.
# Each is handled at most once; a bare Enter on the wrong prompt would quit the session.
handled_trust=0
handled_bypass=0
for _ in $(seq 1 40); do
  pane="$(tmux capture-pane -t "${session}" -p 2>/dev/null || true)"
  if [[ ${handled_trust} -eq 0 ]] && grep -q "trust this folder" <<<"${pane}"; then
    tmux send-keys -t "${session}" Enter
    handled_trust=1
    echo "[audit-dispatch] accepted the folder-trust prompt"
  elif [[ ${handled_bypass} -eq 0 ]] && grep -q "Bypass Permissions mode" <<<"${pane}"; then
    tmux send-keys -t "${session}" Down
    sleep 1
    tmux send-keys -t "${session}" Enter
    handled_bypass=1
    echo "[audit-dispatch] accepted the bypass-permissions warning"
  fi
  sleep 1
done

echo "[audit-dispatch] launched tmux session ${session} — attach with: tmux attach -t ${session}"
