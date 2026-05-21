# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this codebase is

`workstream-mcp` is the MCP server that holds **workflow state** for AI agents and humans collaborating on software work across many products, repos, GitHub Project boards, and users. The full build specification lives in [`workstream-mcp-spec.md`](./workstream-mcp-spec.md) at the repo root — it is implementation-ready and authoritative. **Read it before making non-trivial changes.**

## External documentation

- **Slack platform:** When working on anything touching the Slack integration, start with the documentation index at https://docs.slack.dev/llms.txt — a structured, LLM-oriented overview of the platform. A full page index is at https://docs.slack.dev/llms-sitemap.md.

## Commands

```powershell
# Restore + build everything
dotnet restore
dotnet build

# Run the API host (needs a reachable Postgres)
dotnet run --project src/Workstream.Api

# Unit tests (fast, no Docker)
dotnet test tests/Workstream.UnitTests

# Integration tests (needs Docker — Testcontainers spins up a Postgres)
dotnet test tests/Workstream.IntegrationTests

# Concurrency tests — load-bearing for correctness, must pass in CI on every PR
dotnet test tests/Workstream.ConcurrencyTests

# Single test by name (xUnit)
dotnet test tests/Workstream.UnitTests --filter "FullyQualifiedName~StateMachineServiceTests.RejectsIllegalAuditTransition"

# Admin CLI
dotnet run --project tools/workstream-admin -- user create --github-username moe --type human

# Container stack
docker compose -f deploy/docker-compose.yml up
```

## Architecture, in three paragraphs

**The claim primitive is the system.** Almost every other behavior derives from it. Tasks, findings, and attempts each carry claim columns (`claim_actor_id`, `claim_token`, `claimed_at`, `claimed_until`). Claiming is `SELECT ... FOR UPDATE SKIP LOCKED` inside a single transaction so concurrent claimers never block each other. TTLs come from the plan-type's `role_ttls` JSONB. When an agent dies mid-task its claim simply expires and the work returns to the pool — no background sweeper required for correctness (one runs hourly for cleanliness). See `Workstream.Core/Claims/` and `Workstream.Data/Repositories/TaskRepository.cs`. The concurrency tests in `tests/Workstream.ConcurrencyTests` are the load-bearing correctness gate; if they flake, the merge is blocked.

**The state machine is data, not code.** Legal transitions, allowed roles, role TTLs, retry caps, and board-column mappings live in `plan_types.state_graph` as JSONB. The two seed profiles (`audit`, `development`) are loaded from `deploy/profiles/*.json` by migration `0002_seed_plan_types.sql`. The C# `StateMachineService` validates every transition against the JSONB graph. Adding a new plan type (testing, migration, release management, …) is an `INSERT` into `plan_types`, not a recompile. Resist the urge to hardcode audit-specific transitions in C#.

**Outboxes everywhere.** Anything that talks to an external system (GitHub Projects V2 board sync, Slack notifications) goes through a Postgres outbox: the MCP request transaction commits Postgres state plus an outbox row, then a background worker drains the outbox with retries. This means GitHub or Slack outages never block MCP tool calls — internal state stays correct, external systems catch up when reachable. The outboxes are `board_sync_log` and `slack_notify_log`. See `Workstream.GitHub/BoardSyncWorker.cs` and `Workstream.Slack/SlackNotifyWorker.cs`.

## Non-negotiables when editing

- **The events table is sacred.** Every state-mutating transaction writes to `events`. Append-only at the application layer — never delete rows. Triggers in `0001_initial.sql` block UPDATE/DELETE on `events` and `verdicts`.
- **The URL token is a credential.** Treat with password discipline: never log it, never echo it in errors or traces, never put it in outbound HTTP. Caddy access logs are configured to redact the second path segment; `RequestLoggingMiddleware` strips the path before any log line. The only token surface that should ever expose the literal value is the one-time admin response when creating or rotating.
- **Every external call goes through the outbox.** Never put a GitHub or Slack API call inside the MCP request transaction. If you find yourself reaching for one, you're holding the design wrong.
- **State transitions go through `StateMachineService`.** Don't write status mutations that bypass it. Illegal transitions must return the structured `illegal_transition` error with `allowed_next` populated — orchestrator LLMs read that field to self-correct.

## How to add a new MCP tool

1. Add a class under `src/Workstream.Mcp/Tools/<Group>/` implementing `IMcpTool` (Name, Description, InputSchema, Execute).
2. The Description is read by the calling LLM — write it as a full sentence telling the agent *when* to use the tool, what state the entity must be in, and what the return shape means.
3. Wrap all DB work in a single transaction. Within it: validate claim token if relevant, validate state transition via `StateMachineService`, write business rows, write the `events` row, write outbox rows.
4. Register the tool in `Workstream.Api/Program.cs` (or wherever tool discovery scans).
5. Add an integration test in `tests/Workstream.IntegrationTests/Tools/` that exercises the happy path and the structured-error cases.

## How to add a new plan type

1. Drop a JSON profile in `deploy/profiles/<type>.json` following the shape of `audit.json` / `development.json`.
2. Add a migration `deploy/migrations/000N_seed_<type>.sql` that `INSERT`s into `plan_types`.
3. Add unit tests in `Workstream.UnitTests` exercising legal and illegal transitions for the new profile (see `StateMachineServiceTests`).
4. **Do not** touch C# code. If you find yourself wanting to, the design is degrading — push that need back into the state-graph schema.

## Build sequencing if work is incomplete

The spec's Section 18 orders the work: scaffold → schema → seed plan types → claim primitive + concurrency tests → state machine → MCP tool surface → URL middleware → GitHub V2 board sync → Slack outbox → webhook ingestion → admin CLI → telemetry → deployment artifacts. Earlier items are foundations; do not skip ahead.

## Acceptance criteria

`workstream-mcp-spec.md` §19 defines v1 done. The ten points are the merge gate. The two most likely-to-fail under load are §19.5 (concurrency tests must not flake) and §19.3 (bidirectional board sync without loops — the outbox marker pattern in `BoardSyncWorker` is what prevents echoes).
