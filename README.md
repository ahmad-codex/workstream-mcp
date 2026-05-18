# workstream-mcp

A general-purpose workflow-automation MCP server: the state-of-record for AI-driven and human-driven work across many products, repositories, GitHub Project boards, and users. Replaces per-project markdown plans with a structured store that orchestrator agents and human developers read from and write to concurrently.

Build specification: [`workstream-mcp-spec.md`](./workstream-mcp-spec.md). The spec is the source of truth — this README is a pointer.

## Stack

- .NET 10, ASP.NET Core minimal API
- Postgres 16 via Npgsql + Dapper
- GitHub Projects V2 via Octokit.GraphQL.NET (V2 is GraphQL-only)
- SlackNet for Slack notifications
- Serilog → OpenTelemetry for logs/metrics/traces
- FluentMigrator for schema
- xUnit + Testcontainers for tests

Two containers in production: `postgres` and `api`. No PgBouncer at v1; revisit if pool waits become non-trivial.

## Repository layout

```
src/
  Workstream.Api/         ASP.NET host, MCP transport, HTTP routing, middleware
  Workstream.Core/        Domain models, state machine, claim logic
  Workstream.Data/        Npgsql + Dapper repositories, migration runner
  Workstream.GitHub/      Projects V2 GraphQL client + board sync outbox
  Workstream.Slack/       Slack notification client + outbox
  Workstream.Telemetry/   OTel wiring, metric registrations
  Workstream.Mcp/         MCP tool definitions and dispatch
tests/
  Workstream.UnitTests/
  Workstream.IntegrationTests/   Testcontainers-Postgres
  Workstream.ConcurrencyTests/   32-claimer stress tests
tools/
  workstream-admin/       Admin CLI
deploy/
  migrations/             SQL migrations (FluentMigrator-compatible)
  profiles/               Plan-type JSON profiles
  postgres/, grafana/, prometheus/
docs/
  architecture.md, state-machine.md, mcp-tools.md, deployment.md, onboarding.md
```

## Building locally

```powershell
dotnet restore
dotnet build
dotnet test tests/Workstream.UnitTests
```

Integration and concurrency tests spin up a Postgres container via Testcontainers — Docker must be running:

```powershell
dotnet test tests/Workstream.IntegrationTests
dotnet test tests/Workstream.ConcurrencyTests
```

## Running the API locally

```powershell
$env:WORKSTREAM_DB_CONNECTION = "Host=localhost;Database=workstream;Username=workstream;Password=devpass"
$env:WORKSTREAM_ADMIN_TOKEN = "dev-admin-token"
$env:WORKSTREAM_PUBLIC_BASE_URL = "http://localhost:8080"
dotnet run --project src/Workstream.Api
```

For the full container-based stack: `docker compose -f deploy/docker-compose.yml up`.

## Plug-and-play onboarding

See [`docs/onboarding.md`](./docs/onboarding.md). One admin command creates a user URL; the developer pastes one URL into Claude's MCP config. No PAT, no OAuth, no token prompts.
