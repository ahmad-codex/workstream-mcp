# Architecture

`workstream-mcp` is the state-of-record for AI-driven and human-driven software work across many products, repos, GitHub Project boards, and users. The full design specification is [`workstream-mcp-spec.md`](../workstream-mcp-spec.md); this document summarizes the running shape.

## The system in one paragraph

A .NET 10 ASP.NET Core service backed by Postgres 16 exposes an MCP server over HTTP. Each MCP-using client (a Claude developer session, an orchestrator agent) authenticates by including a 256-bit URL token in the path: `https://mcp.example.com/<token>/mcp`. Tools manipulate a structured store of projects, plans, tasks, findings, attempts, and verdicts. Concurrent claimers use Postgres's `FOR UPDATE SKIP LOCKED` so they never block each other. Every state change writes an `events` row. Board updates (GitHub Projects V2) and Slack notifications go through outbox tables drained by background workers, so external-API hiccups never block MCP calls.

## Module map

```
Workstream.Api          ASP.NET host: middleware (token resolution, rate limit, logging),
                        /mcp JSON-RPC endpoint, /webhooks/github/projects,
                        /webhooks/slack/interactivity, /admin/*. Also the audit-dispatch
                        worker + ProcessAuditRunner (run as a `dispatcher`-role replica).
Workstream.Core         Domain entities (immutable records), state machine, claim primitives,
                        notification enqueue interfaces, errors. No I/O.
Workstream.Data         Npgsql + Dapper repositories, SqlMigrationRunner, PostgresPlanTypeCache,
                        StuckWorkJob hosted service.
Workstream.GitHub       GitHub App auth, ProjectsV2Client (GraphQL + REST: milestones,
                        issues), BoardSyncWorker (board_sync_log + milestone_sync_log),
                        WebhookSignatureVerifier.
Workstream.Slack        SlackClient (chat.postMessage / views.open), SlackBlockKitBuilder +
                        SlackModalBuilder, SlackNotifyWorker (outbox drain),
                        SlackSignatureVerifier, per-project bot-token resolver.
Workstream.Mcp          IMcpTool abstraction, all tool implementations, DI registration.
Workstream.Telemetry    OTel bootstrap, WorkstreamMetrics registrations.
tools/workstream-admin  Operator CLI: user/project/plan management, bootstrap apply.
```

## Hard rules

1. **Claim is the only legitimate path to mutate a row.** Every mutation tool takes a claim token and validates inside the transaction. Override-style mutations are explicit (`override_verdict`, `mark_task_status`) and require permission flags.
2. **State machine is data.** `plan_types.state_graph` JSONB. Adding a plan type is an `INSERT`, not a recompile. Resist hardcoding audit-specific transitions in C#.
3. **Outboxes for every external call.** Anything that talks to GitHub or Slack goes through an outbox table — `board_sync_log` (board items / issues), `milestone_sync_log` (audit milestones), `slack_notify_log` (notifications), `audit_dispatch_log` (audit run/cancel requests). Workers drain them. MCP requests never wait on external APIs. The one inbound exception is `/webhooks/slack/interactivity`, which must answer Slack within 3 s — it verifies the signature and opens the modal synchronously.
4. **Append-only `events` and `verdicts`.** Postgres triggers block UPDATE/DELETE on these tables. Corrections are new rows.
5. **URL token is a credential.** Logged paths redact it. Errors never echo it. The only emission point is the one-time admin response when creating or rotating a user.

## Request lifecycle (one tool call)

1. Caddy terminates TLS, forwards `https://mcp.example.com/<token>/mcp` to the API container with the path intact (and path-logging redacted in Caddy access logs).
2. `RequestLoggingMiddleware` records the request with the token segment rewritten to `<actor:alice>`.
3. `TokenResolutionMiddleware` peels the first path segment, looks the token up in `users`, attaches a `RequestContext` to `HttpContext.Items`, and rewrites the path to drop the token.
4. `RateLimitMiddleware` checks the per-actor sliding-window budget (60/min, 600/hr).
5. `/mcp` JSON-RPC handler dispatches to one of `initialize`, `tools/list`, `tools/call`.
6. For `tools/call`, the tool's typed input is deserialized, the tool's `ExecuteAsync(input, ctx, ct)` runs. The tool opens its own transaction, validates the claim token, validates the state transition via `StateMachineService`, writes business rows, writes the `events` row, writes outbox rows for any external side effects, commits, and returns a typed output.
7. The transport wraps the output in `{ ok, data, error }`.
8. The board-sync and Slack workers drain their outboxes asynchronously, hitting GitHub/Slack with exponential backoff and recording results.

## Concurrency model

The single load-bearing pattern is the claim query (§6 of the spec):

```sql
WITH next AS (
  SELECT id FROM tasks
  WHERE plan_id = $1
    AND ((status='pending' AND claim_token IS NULL)
         OR (status='claimed' AND claim_token IS NOT NULL AND claimed_until < now()))
  ORDER BY priority DESC, created_at
  FOR UPDATE SKIP LOCKED
  LIMIT 1
)
UPDATE tasks SET claim_actor_id=..., claim_token=gen_random_uuid(), ... FROM next ...
RETURNING ...
```

`SKIP LOCKED` is what lets N concurrent claimers race without blocking. The concurrency tests in `tests/Workstream.ConcurrencyTests/ClaimRaceTests.cs` are the load-bearing correctness gate; they run on every PR in CI and a flake blocks the merge.

## Data flow: dev-task happy path

```
developer's Claude session                   workstream-mcp                 GitHub Projects V2     Slack
──────────────────────────                   ───────────────                ──────────────────     ─────
claim_next_task                ─────────────►  tasks UPDATE  + events row
                                               + board_sync_log enqueue       
                                               + slack_notify_log enqueue
                               ◄──────────── { task, claim_token }
                                                                    ─── async drain ───►  card → "In Progress"
                                                                    ─── async drain ───►  Slack message
start_work                     ─────────────►  tasks status → in_progress + event
                                                                    ─── async drain ───►  (already in progress; idempotent)
…coding happens…
submit_attempt                 ─────────────►  attempts INSERT + tasks status → review
                                                                    ─── async drain ───►  card → "Review"
                                                                    ─── async drain ───►  Slack message
…reviewer claims & approves elsewhere…
                                            tasks status → done + commit_recorded event
                                                                    ─── async drain ───►  card → "Done"
                                                                    ─── async drain ───►  Slack thread reply
```

## Pointers

- Spec: [`workstream-mcp-spec.md`](../workstream-mcp-spec.md) is authoritative.
- State machine details: [`state-machine.md`](./state-machine.md).
- MCP tool list: [`mcp-tools.md`](./mcp-tools.md).
- Deployment recipe: [`deployment.md`](./deployment.md).
- Audit dispatcher (run audits fleet-wide): [`audit-dispatch.md`](./audit-dispatch.md).
- One-paste onboarding: [`onboarding.md`](./onboarding.md).
