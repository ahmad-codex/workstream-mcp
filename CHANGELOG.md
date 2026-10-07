# Changelog

All notable changes to this project are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[Semantic Versioning](https://semver.org/).

## [Unreleased]

### Added

- MIT license, contributing guide, code of conduct, security policy, issue and pull request templates.
- `deploy/docker-compose.quickstart.yml`: Postgres and the API only, for local evaluation.
- Dependabot for NuGet and GitHub Actions, and a CodeQL workflow.

### Fixed

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
