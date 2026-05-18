# MCP tools

Every tool is a class in `src/Workstream.Mcp/Tools/` implementing `IMcpTool`. Tools have a typed input record (deserialized from the JSON arguments) and a typed output, wrapped on the wire in `{ ok, data, error }` per §9.7. Descriptions on each tool are deliberately verbose because the calling LLM reads them.

The wire protocol is JSON-RPC 2.0 over HTTP POST to `/mcp`. Three methods: `initialize`, `tools/list`, `tools/call`.

## Discovery and dashboards (§9.1)

| Tool | Purpose |
|---|---|
| `list_projects` | Every project, with active plan count and the caller's open claim count |
| `list_plans` | Plans for one project, optionally filtered by status, with per-status task counts |
| `get_plan_dashboard` | Single-call snapshot: status counts, next claimable, my claims, stuck work, last 20 events |
| `get_my_active_work` | Every claim the caller currently holds across all plans |

## Claim and release (§9.2)

| Tool | Purpose |
|---|---|
| `claim_next_task` | Atomically claim the highest-priority pending task for a role |
| `claim_specific_task` | Claim a specific task by id (fails if held) |
| `claim_next_finding_for_verification` | Audit only — pending-verification finding |
| `claim_next_finding_for_fix` | Audit only — confirmed (or below-cap fix_failed) finding |
| `claim_next_attempt_for_review` | Reserved for future queue model (v1 stub) |
| `release_claim` | Surface work back to the pool without submitting |

## Work submission (§9.3)

| Tool | Purpose |
|---|---|
| `start_work` | claimed → in_progress, triggers board column flip |
| `submit_findings` | Audit only — submit findings; empty list goes to done, non-empty goes to review |
| `submit_verification_verdict` | Audit only — confirmed/rejected/ambiguous on a finding |
| `submit_attempt` | Submit code changes (dev task or audit fix); enforces retry cap |
| `submit_attempt_verdict` | Audit only — fix_confirmed/fix_failed/partial on an attempt |
| `submit_review_decision` | Dev only — approved/changes_requested on a task in review |

## Lifecycle and admin (§9.4)

| Tool | Purpose |
|---|---|
| `mark_task_status` | Set deferred/blocked/skipped/out_of_scope/needs_human_review |
| `record_commit` | Attach a commit hash after the fact |
| `create_task` | Create a new task (orchestrator decomposition) |
| `override_verdict` | Force a state change without a claim — needs `can_override_verdict` |

## Forensic (§9.5)

| Tool | Purpose |
|---|---|
| `get_event_log` | Forensic timeline for one entity |
| `get_stuck_work` | On-demand view of the same data the hourly job computes |
| `export_plan` | markdown or json snapshot of a plan |

## Plan and project setup (§9.6, admin-gated)

| Tool | Purpose |
|---|---|
| `create_project` | New project |
| `add_project_repo` | Attach a GitHub repo |
| `add_project_board` | Register a GitHub Projects V2 board (with discovered option ids) |
| `set_project_slack` | Configure Slack workspace + channel + bot-token secret ref |
| `create_plan` | New plan (in draft) |
| `add_phase` | Add a phase to a plan |
| `activate_plan` | Draft → active; enqueues board items for every task |

## Error codes (§9.7)

`unauthorized`, `not_found`, `stale_claim`, `illegal_transition`, `permission_denied`, `retry_cap_exceeded`, `validation_error`, `conflict`, `external_dependency_error`, `service_unavailable`, `task_unavailable`, `no_work_available`, `role_not_allowed`, `plan_type_unknown`, `guard_failed`.

The `illegal_transition` error always populates `details.allowed_next: string[]` so an LLM caller can self-correct.

## Adding a new tool

1. Create a class under `src/Workstream.Mcp/Tools/<Group>/` extending `McpTool<TInput, TOutput>`.
2. Override `Name`, `Description`, and `RunAsync`. Description is read by the calling LLM — be specific about *when* to use the tool, what state the entity must be in, and the return shape.
3. Inside `RunAsync`, open a transaction (or use a repository method that already does), validate the claim token if relevant, call `StateMachineService.ValidateTransition`, write business rows, write the `events` row, enqueue outbox rows for any external side effects.
4. Register the tool in `McpToolRegistration.AddWorkstreamMcpTools`.
5. Add an integration test under `tests/Workstream.IntegrationTests/Tools/` exercising the happy path and the structured-error cases.
