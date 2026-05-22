#!/usr/bin/env bash
# Audit-dispatch script — invoked by Workstream's AuditDispatchWorker once per queued
# audit (the request_audit MCP tool enqueues one row per project). It checks out the
# project's repo and launches its audit orchestrator inside a DETACHED tmux session, so
# the run can be attached to and steered:
#
#   docker compose -f deploy/docker-compose.yml exec dispatcher tmux attach -t audit-<slug>
#
# The script is baked into the dispatcher image at /app/audit-dispatch.sh. It runs inside
# the dispatcher container, which carries git, tmux and the Claude Code CLI.
#
# Environment provided by the worker:
#   WS_PROJECT_ID    project uuid
#   WS_PROJECT_SLUG  project slug (the local checkout dir name + tmux session suffix)
#   WS_REPO_OWNER    GitHub owner of the project's primary repo
#   WS_REPO_NAME     GitHub repo name
#   WS_REPOS_DIR     base dir for checkouts (default /srv/audit-repos)
#   WS_GIT_TOKEN_FILE  file holding a GitHub token for private clones
#                      (default /run/secrets/gh_clone_token)
#
# A non-zero exit tells the worker the dispatch failed; it retries with backoff.

set -euo pipefail

REPOS_DIR="${WS_REPOS_DIR:-/srv/audit-repos}"
slug="${WS_PROJECT_SLUG:?missing WS_PROJECT_SLUG}"
repo_dir="${REPOS_DIR}/${slug}"
session="audit-${slug}"

echo "[audit-dispatch] project=${slug} repo=${WS_REPO_OWNER:-?}/${WS_REPO_NAME:-?}"

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

# Clone on first run, otherwise fast-forward to latest.
if [[ ! -d "${repo_dir}/.git" ]]; then
  if [[ -z "${WS_REPO_OWNER:-}" || -z "${WS_REPO_NAME:-}" ]]; then
    echo "[audit-dispatch] no repo registered for ${slug}; add one with add_project_repo" >&2
    exit 1
  fi
  echo "[audit-dispatch] cloning ${WS_REPO_OWNER}/${WS_REPO_NAME}"
  git "${git_auth[@]}" clone "https://github.com/${WS_REPO_OWNER}/${WS_REPO_NAME}.git" "${repo_dir}"
fi
git "${git_auth[@]}" -C "${repo_dir}" pull --ff-only \
  || echo "[audit-dispatch] pull skipped (non-ff or offline)"

# The audit runs as the non-root `auditor` user — Claude Code refuses
# --dangerously-skip-permissions under root. Hand it the repo checkout (the fixer role
# edits files) and its own Claude config dir.
chown -R auditor:auditor "${repo_dir}" /home/auditor

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

# Launch the project's audit orchestrator as `auditor` in a detached, attachable tmux
# session. --dangerously-skip-permissions bypasses the per-tool approval prompts so the
# run is unattended (the project's own /audit-run, on first-party code).
tmux new-session -d -s "${session}" -c "${repo_dir}" \
  runuser -u auditor -- env HOME=/home/auditor claude --dangerously-skip-permissions "/audit-run"

# Two startup gates that neither --dangerously-skip-permissions nor config keys reliably
# suppress, answered here so the run proceeds unattended:
#   1. "trust this folder?"        — "Yes" is pre-selected, so Enter confirms.
#   2. "Bypass Permissions mode"   — "No, exit" is pre-selected (!), so send "2" first.
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
    tmux send-keys -t "${session}" 2 Enter
    handled_bypass=1
    echo "[audit-dispatch] accepted the bypass-permissions warning"
  fi
  sleep 1
done

echo "[audit-dispatch] launched tmux session ${session} — attach with: tmux attach -t ${session}"
