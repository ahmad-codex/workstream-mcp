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

# Pre-seed ~/.claude.json so Claude Code's first-run wizard (theme / onboarding) and the
# per-folder "do you trust this directory?" prompt never block the automated run. Auth
# itself lives in the persisted ~/.claude/.credentials.json and is left untouched.
node -e '
  const fs = require("fs"), path = process.env.HOME + "/.claude.json";
  let c = {}; try { c = JSON.parse(fs.readFileSync(path, "utf8")); } catch {}
  c.theme = c.theme || "dark";
  c.hasCompletedOnboarding = true;
  c.projects = c.projects || {};
  c.projects[process.argv[1]] = { ...(c.projects[process.argv[1]] || {}), trusted: true };
  fs.writeFileSync(path, JSON.stringify(c, null, 2));
' "${repo_dir}"

# Launch the project's audit orchestrator in a detached, attachable tmux session.
# The orchestrator (the project's own /audit-run command) talks back to Workstream to
# create the audit plan and tasks.
tmux new-session -d -s "${session}" -c "${repo_dir}" claude "/audit-run"
echo "[audit-dispatch] launched tmux session ${session} — attach with: tmux attach -t ${session}"
