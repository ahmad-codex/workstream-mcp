# Security policy

## Reporting a vulnerability

Please do not open a public issue for security problems.

Report them privately through GitHub: open the repository's **Security** tab and choose
**Report a vulnerability** (GitHub private vulnerability reporting). Include:

- what is affected (endpoint, MCP tool, container, migration)
- steps to reproduce, or a proof of concept
- the impact you expect

You should get an acknowledgement within 7 days. Fixes are released on `master` and noted
in [CHANGELOG.md](./CHANGELOG.md) once a fix is available.

## Supported versions

Only the latest commit on `master` is supported. There are no maintained release branches yet.

## Security model, in short

These are the areas where a report is most useful.

- **URL token as credential.** Each user's MCP endpoint is `/<token>/mcp`. The token is a
  bearer credential. It is stored in `users.mcp_url_token` (plain text today; only revoked
  tokens are stored as SHA-256 hashes), so protect database access and backups accordingly.
  The server rewrites it out of the request path before routing, and
  `RequestLoggingMiddleware` redacts it from logs.
  Anything that leaks a token into logs, traces, error bodies or outbound HTTP is a vulnerability.
- **Admin endpoints.** `/admin/*` requires `Authorization: Bearer <WORKSTREAM_ADMIN_TOKEN>`,
  compared in constant time.
- **Webhooks.** GitHub webhooks are verified with HMAC-SHA256 (`X-Hub-Signature-256`) and
  Slack interactivity with the Slack signing secret and timestamp.
- **Audit log.** Database triggers block `UPDATE` and `DELETE` on `events` and `verdicts`.
- **Audit dispatcher.** The optional `dispatcher` container runs Claude Code with
  `--dangerously-skip-permissions` as a non-root user against checked-out repositories.
  Treat it as code execution on that host and run it only against repositories you trust.

The server has no TLS of its own. Put it behind a TLS-terminating reverse proxy
(`deploy/Caddyfile` is an example) before exposing it beyond localhost.
