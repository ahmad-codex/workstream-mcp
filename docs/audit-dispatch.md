# Audit dispatch

Run a full audit on any project — or many projects — from one place, without checking
out a single repo by hand. `request_audit` is the entry point; a sidecar container does
the work.

## The shape

```
request_audit(["example-app", …])      ← one MCP call
        │
   audit_dispatch_log outbox         ← request_audit only commits a Postgres row
        │
┌──────────────────────────────────┐
│ dispatcher container             │  same image as `api`, WORKSTREAM_ROLE=dispatcher
│   AuditDispatchWorker            │  → registers only the audit-dispatch worker
│   git · tmux · Claude Code       │
│                                  │
│  per queued project:             │
│   git clone/pull the repo        │
│   register workstream + ssh-host │
│     MCP servers for the run      │
│   tmux new-session  claude       │  detached, named  audit-<slug>
│     "/audit-run"                 │
└──────────────────────────────────┘
        │
   the project's own /audit-run orchestrator creates the audit plan,
   tasks, findings — talking back to Workstream as the triggering user
```

The dispatcher is the same .NET image as `api`, started with `WORKSTREAM_ROLE=dispatcher`
so `Program.cs` wires up only `AuditDispatchWorker` — no HTTP surface, no MCP tools, no
migrations, no Slack/GitHub workers. Unlike the lean `api` image it additionally carries
`git`, `tmux`, the Claude Code CLI and `ssh-mcp` (`deploy/Dockerfile.dispatcher`).

The audit runs as a non-root `auditor` user — Claude Code refuses
`--dangerously-skip-permissions` under root. The dispatch script
(`deploy/audit-dispatch.sh`) clones as root (to read the token secret), hands the
checkout to `auditor`, and launches `claude` via `runuser`.

## MCP tools

- **`request_audit`** — queue a run for one or more projects. Each project is resolved by
  slug or id; the result carries a dispatch id and an `attach_command`.
- **`cancel_audit`** — queue a cancellation. The dispatcher interrupts the live session
  (Esc) and sends `/audit-cancel`, so the orchestrator runs its own cleanup.

Both are admin-gated. The triggering user is recorded; the dispatcher resolves *their*
MCP token and registers the workstream MCP as them, so the orchestrator's `create_plan` /
`create_tasks` calls are attributed to whoever asked for the audit.

## Per-project requirement

Each target repo must provide two Claude Code commands:

- **`/audit-run`** — the audit orchestrator (creates the plan, decomposes tasks, drives
  the auditor → verifier → fixer → fix-verifier loop).
- **`/audit-cancel`** — the cleanup routine `cancel_audit` triggers: release Workstream
  claims, prune worktrees, then call `archive_plan` with `Outcome: Canceled`.

Bind the repo to the project first with `add_project_repo`.

## Watching a run

`request_audit` returns the command; once SSH'd into the host:

```bash
docker exec -it deploy-dispatcher-1 tmux attach -t audit-<project-slug>
```

Detach with `Ctrl+B` then `D` — the audit keeps running.

## Configuration

Set on the `dispatcher` service (see `deploy/docker-compose.yml`):

| Variable | Purpose |
|---|---|
| `WORKSTREAM_ROLE=dispatcher`         | Selects dispatcher mode |
| `WORKSTREAM_AUDIT_DISPATCH_ENABLED`  | `true` to poll the outbox (the `api` role leaves it off) |
| `WORKSTREAM_AUDIT_DISPATCH_SCRIPT`   | Path to the dispatch script (baked in at `/app/audit-dispatch.sh`) |
| `WS_REPOS_DIR`                       | Base dir for repo checkouts (a persistent volume) |
| `WS_SSH_MCP_HOST` / `WS_SSH_MCP_USER` | Optional test host for the `ssh-host` MCP given to audit runs; unset to skip it |

Secrets the dispatcher needs: `pg_password`, `gh_clone_token` (private clones),
`ssh_mcp_key` + `ssh_mcp_passphrase` (the optional `ssh-host` MCP, enabled by `WS_SSH_MCP_HOST`). See
[deployment.md](./deployment.md#secrets).

## One-time Claude Code login

The dispatcher authenticates Claude Code via an interactive login, persisted to the
`claude-config` volume (`/home/auditor/.claude`) — no API key:

```bash
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.prod.yml \
  exec -u auditor -it dispatcher claude
# then: /login
```

The login survives container restarts and rebuilds.

## Milestones and issues

An audit run drives a GitHub repo milestone — see [github-setup.md](./github-setup.md#audit-milestones).
