# Deployment

Containers on a single Linux host, behind a TLS reverse proxy (§12):

| Container | Role |
|---|---|
| `postgres`   | Postgres 16 — the state store |
| `pgbouncer`  | Transaction-pool connection multiplexer |
| `api`        | The MCP server + background workers (board sync, Slack, audit dispatch is idle here) |
| `dispatcher` | Same image as `api` run with `WORKSTREAM_ROLE=dispatcher` — runs only the audit-dispatch worker; carries git, tmux and Claude Code. See [audit-dispatch.md](./audit-dispatch.md) |

## One-time setup

1. Provision a Linux host.
2. Install Docker + Docker Compose plugin.
3. Install Caddy on the host (not in a container — it runs the public TLS endpoint).
4. Create the `workstream-mcp` directory and clone this repo into it.

## Secrets

Drop the following into `deploy/secrets/` (the `.gitignore` excludes everything except the README):

| File | Contents |
|---|---|
| `pg_password`           | Postgres password (single line, no trailing newline) |
| `admin_token`           | Static bearer for `/admin/*` |
| `gh_app_key.pem`        | GitHub App private key |
| `gh_webhook_secret`     | GitHub webhook signing secret |
| `slack-<project>`       | One per project — the Slack bot token |
| `slack_signing_secret`  | Slack app signing secret — verifies inbound interactivity (the "View details" modal) |
| `gh_clone_token`        | GitHub token the dispatcher uses to clone private project repos |
| `ssh_mcp_key`           | SSH private key for the dispatcher's optional `ssh-host` MCP (set `WS_SSH_MCP_HOST` to enable it) |
| `ssh_mcp_passphrase`    | Passphrase for `ssh_mcp_key` |

Update `deploy/docker-compose.yml` to list any additional Slack secrets you add. The
`slack_signing_secret`, `gh_clone_token`, `ssh_mcp_key` and `ssh_mcp_passphrase` secrets
are only needed once the audit dispatcher and Slack interactivity are in use.

## Caddy config

```caddyfile
mcp.example.com {
  reverse_proxy api:8080
  encode gzip zstd
  header Strict-Transport-Security "max-age=63072000; includeSubDomains; preload"
  header Referrer-Policy "no-referrer"

  log {
    output file /var/log/caddy/workstream.log
    format json
    fields { uri delete }    # critical: never log the URL path (contains the token)
  }
}
```

The full template is [`deploy/Caddyfile`](../deploy/Caddyfile).

## Bring it up

Always deploy with **both** compose files — the base plus the production overlay:

```bash
cd workstream-mcp
docker compose -f deploy/docker-compose.yml -f deploy/docker-compose.prod.yml up -d --build
```

`docker-compose.prod.yml` adds the public `0.0.0.0:3010` binding and the prod base URL.
Deploying with the base file alone binds the api to `127.0.0.1` only — the public
endpoint silently goes dark. The redeploy step on the host is `git pull --ff-only` then
the command above.

The API container runs migrations on startup (the `SqlMigrationRunner` in `Workstream.Data/Migrations/` applies `deploy/migrations/*.sql` in numeric order). Re-running is a no-op when versions are already in `__migrations`.

## Smoke

```bash
WORKSTREAM_BASE=https://mcp.example.com \
WORKSTREAM_TEST_TOKEN=<a-test-user-token> \
WORKSTREAM_TEST_PLAN_ID=<a-pre-created-dev-plan-id> \
./scripts/smoke.sh
```

The smoke script drives one task from pending to review through the MCP surface and reports failures.

## Telemetry

Set `WORKSTREAM_OTEL_ENDPOINT=https://otel-collector:4317` in the environment to ship traces, metrics, and logs to your OTel-compatible backend. Grafana dashboard and Prometheus alerts live in [`deploy/grafana/`](../deploy/grafana/workstream.json) and [`deploy/prometheus/`](../deploy/prometheus/workstream-alerts.yml).

## Slack notifications

Per-project setup walkthrough (Slack app creation, scopes, channel invite, token-into-repo, MCP registration, verification) in [`slack-setup.md`](./slack-setup.md).

## GitHub Projects V2 board

Per-project board wiring (App creation under your org, permissions, install + IDs, board discover, plan binding, end-to-end test) in [`github-setup.md`](./github-setup.md).

## Audit dispatcher

The `dispatcher` container runs full audits across projects from a single `request_audit`
call. Its architecture, configuration, secrets, and the one-time Claude Code login are in
[`audit-dispatch.md`](./audit-dispatch.md).

## Backups

`pg_dump` from a cron on the host, nightly, to a separate volume. Weekly off-host copy. The `events` table is part of the dump and durability matters as much as the primary state.

```bash
# /etc/cron.daily/workstream-pg-dump
docker compose -f /srv/workstream-mcp/deploy/docker-compose.yml exec -T postgres \
  pg_dump -U workstream workstream | gzip > /backup/workstream-$(date +%F).sql.gz
```
