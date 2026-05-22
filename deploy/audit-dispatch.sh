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

# Clone on first run, otherwise fast-forward to latest.
if [[ ! -d "${repo_dir}/.git" ]]; then
  if [[ -z "${WS_REPO_OWNER:-}" || -z "${WS_REPO_NAME:-}" ]]; then
    echo "[audit-dispatch] no repo registered for ${slug}; add one with add_project_repo" >&2
    exit 1
  fi
  echo "[audit-dispatch] cloning ${WS_REPO_OWNER}/${WS_REPO_NAME}"
  git clone "https://github.com/${WS_REPO_OWNER}/${WS_REPO_NAME}.git" "${repo_dir}"
fi
git -C "${repo_dir}" pull --ff-only || echo "[audit-dispatch] pull skipped (non-ff or offline)"

# Launch the project's audit orchestrator in a detached, attachable tmux session.
# The orchestrator (the project's own /audit-run command) talks back to Workstream to
# create the audit plan and tasks.
tmux new-session -d -s "${session}" -c "${repo_dir}" claude "/audit-run"
echo "[audit-dispatch] launched tmux session ${session} — attach with: tmux attach -t ${session}"
