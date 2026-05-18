# Deployment

Two containers on a Hetzner host: `postgres` and `api`, behind Caddy for TLS (§12).

## One-time setup

1. Provision the host (already done if you have a working `hetzner-moelabs` deploy).
2. Install Docker + Docker Compose plugin.
3. Install Caddy on the host (not in a container — it runs the public TLS endpoint).
4. Create the `workstream-mcp` directory and clone this repo into it.

## Secrets

Drop the following into `deploy/secrets/` (the `.gitignore` excludes everything except the README):

| File | Contents |
|---|---|
| `pg_password`        | Postgres password (single line, no trailing newline) |
| `admin_token`        | Static bearer for `/admin/*` |
| `gh_app_key.pem`     | GitHub App private key |
| `gh_webhook_secret`  | GitHub webhook signing secret |
| `slack-<project>`    | One per project — the Slack bot token |

Update `deploy/docker-compose.yml` to list any additional Slack secrets you add.

## Caddy config

```caddyfile
mcp.trycrbrl.xyz {
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

```bash
cd workstream-mcp
docker compose -f deploy/docker-compose.yml build
docker compose -f deploy/docker-compose.yml up -d
```

The API container runs migrations on startup (the `SqlMigrationRunner` in `Workstream.Data/Migrations/` applies `deploy/migrations/*.sql` in numeric order). Re-running is a no-op when versions are already in `__migrations`.

## Smoke

```bash
WORKSTREAM_BASE=https://mcp.trycrbrl.xyz \
WORKSTREAM_TEST_TOKEN=<a-test-user-token> \
WORKSTREAM_TEST_PLAN_ID=<a-pre-created-dev-plan-id> \
./scripts/smoke.sh
```

The smoke script drives one task from pending to review through the MCP surface and reports failures.

## Telemetry

Set `WORKSTREAM_OTEL_ENDPOINT=https://otel-collector:4317` in the environment to ship traces, metrics, and logs to your OTel-compatible backend. Grafana dashboard and Prometheus alerts live in [`deploy/grafana/`](../deploy/grafana/workstream.json) and [`deploy/prometheus/`](../deploy/prometheus/workstream-alerts.yml).

## Slack notifications

Per-project setup walkthrough (Slack app creation, scopes, channel invite, token-into-repo, MCP registration, verification) in [`slack-setup.md`](./slack-setup.md).

## Backups

`pg_dump` from a cron on the host, nightly, to a separate volume. Weekly off-host copy. The `events` table is part of the dump and durability matters as much as the primary state.

```bash
# /etc/cron.daily/workstream-pg-dump
docker compose -f /srv/workstream-mcp/deploy/docker-compose.yml exec -T postgres \
  pg_dump -U workstream workstream | gzip > /backup/workstream-$(date +%F).sql.gz
```
