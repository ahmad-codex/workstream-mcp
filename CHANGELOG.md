# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- Generic example audit orchestrator in `orchestrators/example/`.
- `workstream-admin user create --admin`.
- Unit tests for token redaction in traces and for the admin CLI request bodies.
- MIT license, contributing guide, code of conduct, security policy, issue and pull request templates.
- `deploy/docker-compose.quickstart.yml`: Postgres and the API only, for local evaluation.
- Dependabot for NuGet and GitHub Actions, and a CodeQL workflow.

### Changed

- Docs, spec, deploy files and examples use neutral placeholders (`example-org`, `example.com`,
  `YOUR_SERVER_IP`). The seed template is now `deploy/migrations/0007_seed_example.sql.example`.
- The dispatcher's optional SSH MCP is named `ssh-host` and is configured with
  `WS_SSH_MCP_HOST` / `WS_SSH_MCP_USER`; it is skipped when no host is set.
- `workstream-admin`: removed the `project`, `plan` and `plan-type` commands, which called
  endpoints the server does not have. Use the admin MCP setup tools instead.

### Fixed

- OpenTelemetry is now wired into the API host when `WORKSTREAM_OTEL_ENDPOINT` is set, with the
  URL token redacted from `url.path` on server spans.
- The hourly `StuckWorkJob` is registered in the API role.
- `workstream-admin user create` sent snake_case fields that the server ignored.
- CI now runs on pushes and pull requests to `master` (it targeted `main`, which does not exist).

## [0.1.0] - 2026-05-23

First version. Built between 2026-05-18 and 2026-05-23.

### Added

- MCP server over HTTP (JSON-RPC 2.0) at `/<token>/mcp`, with per-user URL tokens, token
  rotation and redaction of the token from paths and logs.
- Postgres 16 schema with plain SQL migrations applied on startup, accessed through Npgsql and
  Dapper, behind PgBouncer in transaction-pool mode.
- Data-driven state machine: plan types stored as JSONB state graphs, with `audit` and
  `development` seed profiles.
- Claim primitive using `SELECT ... FOR UPDATE SKIP LOCKED`, role-based TTLs, `refresh_claim`
  and `release_claim`, with a concurrency test suite.
- 36 MCP tools for discovery, claiming, submission, lifecycle, forensics, project and plan setup,
  and audit dispatch.
- Task dependencies and bulk task creation (`create_tasks`).
- Append-only `events` and `verdicts` tables enforced by triggers.
- GitHub Projects V2 board sync through an outbox: tasks as issues on repo-backed projects,
  draft items otherwise, column and assignee sync, audit milestones, signed inbound webhooks.
- Slack notifications through an outbox: Block Kit cards, per-task threads, role personas
  selectable per call with `as_agent`, and a signed interactivity endpoint for the details modal.
- `archive_plan` with Completed or Canceled outcomes.
- Audit dispatcher sidecar: `request_audit` and `cancel_audit` queue runs that a separate
  container executes with Claude Code in tmux sessions.
- `workstream-admin` CLI and `/admin/*` endpoints.
- Docker images and compose files for production, Caddy, Grafana and Prometheus configuration.
- Unit, integration (Testcontainers) and concurrency test projects, and a GitHub Actions workflow.

[Unreleased]: https://github.com/ahmad-codex/workstream-mcp/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ahmad-codex/workstream-mcp/releases/tag/v0.1.0
