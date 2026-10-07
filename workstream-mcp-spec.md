# Workstream MCP — Build Specification

A general-purpose workflow automation MCP server. The state-of-record and coordination layer for AI-driven and human-driven work across many products, many repositories, many GitHub Project boards, and many users. Replaces per-project markdown plans with a structured store that orchestrator agents and human developers read from and write to concurrently without stepping on each other.

This document is the build specification for Claude Code. It is implementation-ready: every section ends with concrete artifacts to produce. Build in the order presented in Section 18 — schema first, claims second, state machine third, tools fourth.

---

## 1. Repository

**Suggested name: `workstream-mcp`**

Rationale: the system manages workstreams (audit, development, testing, refactor, migration — any structured stream of tasks). The `-mcp` suffix signals it is the MCP server, not a client. Avoid product-coupled names — generality is the requirement.

**Stack**
- .NET 10
- ASP.NET Core minimal API host
- Npgsql for Postgres
- Dapper for the data layer (the queries are simple; an ORM is overkill and obstructs the `FOR UPDATE SKIP LOCKED` patterns the claim model depends on)
- Octokit.GraphQL.NET for GitHub Projects V2 (the V2 board API is GraphQL-only; the REST Octokit cannot move cards between V2 status fields)
- SlackNet for Slack Web API
- Serilog → OpenTelemetry exporter for logs, metrics, traces
- FluentMigrator for schema migrations
- xUnit + Testcontainers for integration tests (real Postgres in a throwaway container)

**Top-level layout**
```
workstream-mcp/
  src/
    Workstream.Api/                 ASP.NET host, MCP transport, HTTP routing
    Workstream.Core/                Domain models, state machine, claim logic
    Workstream.Data/                Npgsql + Dapper repositories, migrations
    Workstream.GitHub/              Projects V2 GraphQL client and board sync
    Workstream.Slack/               Slack notification client
    Workstream.Telemetry/           OTel wiring, metrics, structured logging
    Workstream.Mcp/                 MCP tool definitions and dispatch
  tests/
    Workstream.UnitTests/
    Workstream.IntegrationTests/    Testcontainers-Postgres-backed
    Workstream.ConcurrencyTests/    Many-claimer stress tests
  deploy/
    docker-compose.yml              Two services: postgres + api
    Dockerfile.api
    migrations/                     SQL migration files
  docs/
    architecture.md
    state-machine.md
    mcp-tools.md
    deployment.md
  README.md
```

**Deliverable for this section**: empty repo with the layout above, `global.json` pinning .NET 10, baseline `Directory.Packages.props` with the listed dependencies, and a placeholder `README.md` that points to this spec.

---

## 2. Operating Model and Naming

The system is generic; it does not know about audits specifically. The vocabulary in the data model is:

| Term | Meaning |
|---|---|
| Organization | Top-level tenant. Most deployments have exactly one. |
| Project | A product under management. Has zero or more GitHub repos, zero or more GitHub Project boards, a Slack workspace, a Slack channel. |
| Plan | A versioned scope of work under a project. `audit`, `development`, `testing`, `refactor`, `migration` are plan **types**. A project can have many plans active at once. |
| Phase | An ordered group of tasks within a plan. Optional structural layer. |
| Task | The atomic unit of work. Equivalent to a card on the GitHub board. Carries lifecycle state, claim metadata, evidence. |
| Finding | A sub-entity of a task, used by audit-type plans. A task can produce zero or more findings; each finding has its own lifecycle. Dev-type plans typically have zero findings per task. |
| Attempt | A sub-entity of a finding (audit plans) or a task (dev plans), representing one pass at resolving the work. Capped retry count is per-plan-type config. |
| Verdict | Append-only outcome record attached to a finding, attempt, or task review. |
| Event | Append-only universal log. Every state change writes an event. |
| Actor | A participant: human, orchestrator agent, subagent. Keyed by GitHub username. |
| Claim | A time-bounded reservation of a task / finding / attempt by an actor in a specific role. |

**Plan types are data, not code.** A `plan_types` table holds the state graph, the allowed roles, the claim TTLs per role, the retry caps, and the prompt template references. Adding "testing" later means inserting a row, not changing the codebase. Sections 5 and 6 specify two seed profiles: `audit` and `development`.

---

## 3. Multi-Tenant Identity: URL-as-Credential

The user requirement is: `mcp.example.com/<uniqueId>` per user, no traditional authentication, the unique id IS the authentication. This is a bearer-token-in-path model. It works for the threat model (trusted internal team, no public exposure to unknown actors), and it is plug-and-play in the sense that a new developer pastes one URL into their Claude config and is done. The spec below makes the token strong enough to survive that role.

### 3.1 Token format and properties

- Each user row has a `mcp_url_token` column: 32 bytes of cryptographically random data, URL-safe base64-encoded (44 chars after stripping padding). Entropy: 256 bits. Brute-force enumeration is not a viable attack vector.
- Tokens are **not derived from** the GitHub username or any other guessable input. The GitHub username is stored separately for attribution.
- Tokens are **revocable and regenerable**. A regeneration writes the old token to a `revoked_tokens` table with a 30-day TTL (so a leaked token used after revocation produces a clear telemetry signal rather than a silent 404).
- The token is the path segment, not a query parameter. Query parameters leak to referrer headers; path segments leak in fewer places but the system still treats them as sensitive (see Section 14).

### 3.2 Resolution flow

When a request arrives at `https://mcp.example.com/<token>/...`:
1. The HTTP host strips the token from the path and looks up the user in a `users` table indexed on `mcp_url_token` (unique B-tree index).
2. If not found, return 404. Do not return 401 — 401 leaks the existence of the namespace. 404 is indistinguishable from "no such route."
3. If found and active, attach the `actor_id` and `actor_type` to the ambient `RequestContext` for the lifetime of the request. Every subsequent DB write and event emission uses this actor.
4. Log the resolution to OTel with the actor_id but **never log the token itself**, not even at TRACE level.

### 3.3 Token lifecycle

- **Issue**: admin endpoint `POST /admin/users` creates a user record, generates a token, returns the full URL once. The full URL is shown exactly once in the admin UI / CLI output; subsequent reads show only the user's metadata, not the token.
- **Rotate**: `POST /admin/users/{userId}/rotate-token`. The old token goes to `revoked_tokens`, the new one is returned once.
- **Revoke**: `POST /admin/users/{userId}/revoke`. The token is moved to `revoked_tokens` and the user record is marked inactive. The user must be re-issued to get a new URL.
- **Audit**: every token use writes a row to `token_usage` (sampled, not every request) for forensic queries.

### 3.4 Admin authentication

The `/admin/*` endpoints use a separate mechanism: a single static admin bearer token loaded from environment variable `WORKSTREAM_ADMIN_TOKEN`. This is the only conventional secret in the system. For v1 this is sufficient; the threat model does not require an admin RBAC system.

### 3.5 Why this is acceptable security

The threat model is: small trusted team, internal use only, the public DNS hostname is the only externally-visible surface. Token strength (256 bits) puts brute-force outside any practical attack budget. The remaining risks — log leakage, browser history, accidental sharing — are mitigated by the operational rules in Section 14. If the threat model later expands (public-facing, untrusted users), the token model is replaced with GitHub OAuth without changing the data model; only the resolution middleware changes.

---

## 4. Data Model

DDL is the authoritative artifact for this section. The English description below is for review; the migration file in `deploy/migrations/0001_initial.sql` is what gets implemented.

### 4.1 Tables (organization-and-tenancy layer)

**`organizations`** — `id uuid pk`, `name text unique`, `created_at timestamptz`.

**`users`** — the actor table for humans and bots.
- `id uuid pk`
- `github_username citext unique not null` (citext for case-insensitive uniqueness)
- `display_name text`
- `mcp_url_token text unique not null` (256-bit base64url, indexed)
- `actor_type text not null check (actor_type in ('human','orchestrator','subagent'))`
- `is_active boolean not null default true`
- `can_override_verdict boolean not null default false`
- `can_archive_plan boolean not null default false`
- `can_mark_needs_human_review boolean not null default true`
- `created_at timestamptz`, `last_seen_at timestamptz`

**`revoked_tokens`** — `token_hash text pk` (SHA-256 of the revoked token; we store the hash, not the token), `user_id uuid fk`, `revoked_at timestamptz`, `expires_at timestamptz`.

**`token_usage`** — sampled (1 in N) request log: `id bigserial pk`, `user_id uuid fk`, `at timestamptz`, `tool_name text`, `success boolean`, `ip inet`.

### 4.2 Tables (project-and-plan layer)

**`projects`**
- `id uuid pk`
- `organization_id uuid fk`
- `slug text unique not null` (e.g. `example-app`, `billing-service`)
- `display_name text not null`
- `description text`
- `config jsonb not null default '{}'` (project-specific conventions: naming, conventions doc URLs, reference repo URLs)
- `created_at timestamptz`

**`project_repos`** — many repos per project.
- `id uuid pk`
- `project_id uuid fk`
- `github_owner text not null`
- `github_repo text not null`
- `default_branch text not null default 'main'`
- `is_reference_only boolean not null default false` (e.g. an upstream project used as a design reference)
- `unique (project_id, github_owner, github_repo)`

**`project_boards`** — many GitHub Project (V2) boards per project. A repo can map to one or more boards.
- `id uuid pk`
- `project_id uuid fk`
- `github_project_v2_node_id text unique not null` (GraphQL node id, format `PVT_*`)
- `github_project_number integer not null`
- `github_owner text not null` (the project owner, may be a user or org)
- `display_name text not null`
- `status_field_node_id text not null` (the Status single-select field, format `PVTSSF_*`)
- `status_option_backlog text not null` (option id within the status field, format `PVTSO_*` or the option name)
- `status_option_in_progress text not null`
- `status_option_review text not null`
- `status_option_done text not null`
- `status_option_blocked text` (optional)
- `config jsonb not null default '{}'`

**`project_slack`** — Slack workspace and default channel per project.
- `project_id uuid pk fk`
- `workspace_id text not null` (Slack team id, `T*`)
- `bot_token_secret_ref text not null` (reference into the secrets store, not the token itself; see Section 14)
- `default_channel_id text not null` (`C*`)
- `notify_on text[] not null default array['task.claimed','task.in_progress','task.review','task.done','task.blocked','finding.confirmed','fix.failed']`

**`plan_types`** — config-driven plan profiles.
- `id text pk` (`audit`, `development`, `testing`, ...)
- `display_name text`
- `state_graph jsonb not null` (legal transitions; see Section 5)
- `role_ttls jsonb not null` (`{"auditor": "30m", "verifier": "15m", ...}`)
- `retry_cap integer not null default 3`
- `prompt_template_ref text` (URL or repo path to the orchestrator prompt for this plan type)
- `requires_findings boolean not null default false` (audit=true, dev=false)
- `board_column_mapping jsonb not null` (which internal task statuses map to which board columns; see 7.4)

**`plans`**
- `id uuid pk`
- `project_id uuid fk`
- `plan_type_id text fk`
- `name text not null`
- `objective text`
- `status text not null check (status in ('draft','active','paused','completed','archived'))`
- `created_by_actor_id uuid fk`
- `primary_board_id uuid fk` (which board this plan posts to)
- `primary_slack_channel_id text` (override of project default)
- `config jsonb not null default '{}'`
- `created_at timestamptz`, `activated_at timestamptz`, `completed_at timestamptz`

### 4.3 Tables (work-graph layer)

**`phases`** — `id uuid pk`, `plan_id uuid fk`, `order_index int not null`, `name text not null`, `status text not null`, `unique (plan_id, order_index)`.

**`tasks`** — the atomic work unit.
- `id uuid pk`
- `plan_id uuid fk`
- `phase_id uuid fk null`
- `external_key text not null` (stable human-readable id, e.g. `T-phase2-feature5`)
- `title text not null`
- `description text`
- `paths text[]` (files / modules covered)
- `reference_pointer text` (e.g. a secrets-manager path for an audit, or a design-doc URL for a dev task)
- `priority int not null default 0`
- `status text not null` (see state machine)
- `assignee_actor_id uuid fk null` (set on first claim; persists for board sync even after claim release)
- `github_board_item_id text` (the V2 item node id, `PVTI_*`)
- `github_issue_number int null` (if the task is also an issue)
- `github_issue_node_id text null`
- `claim_actor_id uuid fk null`
- `claim_role text null`
- `claim_token uuid null` (the claim presentation token; differs from the URL token)
- `claimed_at timestamptz null`
- `claimed_until timestamptz null`
- `created_at timestamptz`
- `unique (plan_id, external_key)`
- index on `(plan_id, status, priority desc)` to speed up `claim_next_task`

**`findings`** — `id uuid pk`, `task_id uuid fk`, `external_key text` (e.g. `F-2-5-1`), `severity text`, `invariant_impact text`, `symptom text`, `root_cause text`, `repro_steps text`, `adversarial_input text`, `expected text`, `actual text`, `reference_comparison text`, `status text`, claim columns identical to `tasks`, `created_at timestamptz`, `unique (task_id, external_key)`.

**`verdicts`** — `id uuid pk`, `finding_id uuid fk null`, `attempt_id uuid fk null`, `verdict_type text not null` (`confirmed`, `rejected`, `ambiguous`, `fix_confirmed`, `fix_failed`, `partial`), `reason_category text`, `pre_output text`, `post_output text`, `adversarial_output text`, `invariant_evidence jsonb`, `actor_id uuid fk`, `at timestamptz`. Append-only: corrections are new rows. Check: exactly one of `finding_id` or `attempt_id` is set.

**`attempts`** — `id uuid pk`, `finding_id uuid fk null`, `task_id uuid fk null`, `attempt_number int not null`, `files_changed text[]`, `approach_summary text`, `side_effects text`, `build_command text`, `test_scenario text`, `diff_ref text` (e.g. `commit:<sha>` or `patch:<id>`), `commit_hash text null`, claim columns, `actor_id uuid fk`, `created_at timestamptz`. Check: exactly one of `finding_id` or `task_id` is set.

### 4.4 Tables (cross-cutting)

**`events`** — append-only universal log.
- `id bigserial pk`
- `at timestamptz not null default now()`
- `actor_id uuid fk`
- `entity_type text not null` (`task`, `finding`, `attempt`, `plan`, `phase`)
- `entity_id uuid not null`
- `event_type text not null` (`claimed`, `released`, `status_changed`, `verdict_submitted`, `attempt_submitted`, `override`, `board_synced`, `slack_notified`, ...)
- `from_state text`, `to_state text`
- `payload jsonb not null default '{}'`
- index on `(entity_type, entity_id, at desc)` for forensic queries
- index on `(at desc)` for global tail

**`board_sync_log`** — per-board outbox/idempotency record. The board sync is best-effort but recorded so we can replay.
- `id bigserial pk`
- `task_id uuid fk`
- `board_id uuid fk`
- `attempted_at timestamptz`
- `target_column text not null`
- `result text not null` (`success`, `retry`, `failed`)
- `error text null`
- `github_response_id text null`
- index on `(task_id, attempted_at desc)`

**`slack_notify_log`** — same shape, for Slack: `id`, `event_id fk events.id`, `channel_id`, `attempted_at`, `result`, `error`, `slack_ts`.

### 4.5 Constraints and triggers

- All claim-bearing rows: a partial unique index `(id) where claim_token is not null` is not needed (claim_token alone is enough). But there should be a check constraint: `(claim_actor_id is null) = (claim_token is null) = (claimed_at is null) = (claimed_until is null)` — claim columns are all-or-nothing.
- An after-insert trigger on `tasks`, `findings`, `attempts` writes a `created` event automatically. Keeps event-emission honest.
- An after-update trigger emits `status_changed` when `status` changes.

**Deliverable for this section**: a single migration file `0001_initial.sql` containing the full schema, all check constraints, all indexes, and the two triggers. A second file `0002_seed_plan_types.sql` seeds the `audit` and `development` plan-type rows (Section 5).

---

## 5. State Machines (Data-Driven)

The state graph lives in `plan_types.state_graph` as JSONB. The C# state-machine service loads it once per plan type at startup and on cache-invalidation events. Adding a new plan type means adding a row, not recompiling.

### 5.1 Shape of the state graph

```jsonc
{
  "task_states": ["pending","claimed","in_progress","review","done","deferred","blocked","needs_human_review"],
  "task_initial": "pending",
  "task_terminal": ["done","deferred","needs_human_review"],
  "task_transitions": [
    { "from": "pending", "to": "claimed", "via": "claim_task", "requires_role": ["auditor","developer"] },
    { "from": "claimed", "to": "in_progress", "via": "start_work" },
    { "from": "in_progress", "to": "review", "via": "submit_for_review" },
    { "from": "review", "to": "done", "via": "approve_review" },
    { "from": "review", "to": "in_progress", "via": "request_changes" },
    { "from": "*", "to": "blocked", "via": "mark_blocked", "requires_permission": "can_mark_needs_human_review" },
    { "from": "blocked", "to": "in_progress", "via": "unblock" },
    { "from": "*", "to": "deferred", "via": "defer", "requires_permission": "can_override_verdict" },
    { "from": "*", "to": "needs_human_review", "via": "escalate" }
  ],
  "finding_states": ["pending_verification","confirmed","rejected","ambiguous","in_fix","fixed","fix_failed","partial","needs_human_review"],
  "finding_transitions": [ ... ],
  "board_column_mapping": {
    "pending": "Backlog",
    "claimed": "Backlog",
    "in_progress": "In Progress",
    "review": "Review",
    "done": "Done",
    "blocked": "Blocked",
    "deferred": "Done",
    "needs_human_review": "Blocked"
  }
}
```

The board mapping in 7.4 takes precedence if defined on the board itself, falling back to this default per plan type.

### 5.2 The two seed plan types

**`audit` profile** matches a find, verify, fix, re-verify audit workflow. Roles: `auditor`, `verifier`, `fixer`, `fix_verifier`. TTLs: 30m / 15m / 45m / 15m. `requires_findings: true`. Retry cap: 3. The finding state machine and the per-finding sub-lifecycle are generalized: a project-specific invariant gate becomes a per-plan configurable `pre_fix_gate` JSONB that the fixer subagent is given as policy, not a hardcoded rule.

**`development` profile**. Roles: `developer`, `reviewer`. TTLs: 4h / 1h. `requires_findings: false`. Retry cap: 3 (number of review rounds). No verifier/fix-verifier split — a dev task goes pending → claimed → in_progress → review → done, with the reviewer either approving (→ done) or requesting changes (→ in_progress, attempt counter increments).

### 5.3 Enforcement

The state machine is enforced **in the MCP layer**, not by Postgres check constraints. Reason: legal transitions evolve more often than the schema, and putting them in JSONB makes them changeable without migration. Postgres enforces:
- Status values are valid (`check (status = any(array[...]))` derived from the union of all plan-type state lists).
- Claim columns are all-or-nothing.
- Append-only invariants (no UPDATE on `verdicts`, `events` — enforced by row-level triggers that raise on UPDATE).

The C# `StateMachineService` exposes `ValidateTransition(planType, entityType, from, to, viaTool, actor) -> Result`. Every mutating MCP tool calls this before applying the transition. Illegal transitions return a structured error: `{ error: "illegal_transition", from, to, allowed_next: [...] }`.

**Deliverable for this section**: `Workstream.Core/StateMachine/StateGraph.cs`, `StateMachineService.cs`, unit tests exhaustively covering legal and illegal transitions for both seed profiles. The seed migration `0002_seed_plan_types.sql` contains the two profiles as JSONB literals.

---

## 6. Concurrency: The Claim Primitive

This section is non-negotiable. Get it right and the rest of the system is straightforward. Get it wrong and you have data corruption under load.

### 6.1 Claiming next task

```sql
-- Inside a single transaction, isolation level READ COMMITTED.
WITH next AS (
  SELECT id
  FROM tasks
  WHERE plan_id = $1
    AND status = 'pending'
    AND (claim_token IS NULL OR claimed_until < now())
  ORDER BY priority DESC, created_at ASC
  FOR UPDATE SKIP LOCKED
  LIMIT 1
)
UPDATE tasks t
SET claim_actor_id = $2,
    claim_role = $3,
    claim_token = gen_random_uuid(),
    claimed_at = now(),
    claimed_until = now() + $4::interval,
    status = 'claimed'
FROM next
WHERE t.id = next.id
RETURNING t.*, t.claim_token AS issued_claim_token;
```

Properties:
- `SKIP LOCKED` means concurrent claimers never block each other; each gets a different row or zero rows.
- The TTL (`$4`) is role-specific from `plan_types.role_ttls`.
- A claim with `claimed_until < now()` is auto-reclaimable. No background sweeper needed for correctness; one runs hourly for cleanliness (Section 9).
- The returned `claim_token` is what the agent presents to release, mutate, or convert the claim.

### 6.2 Claiming a specific task

`claim_specific_task(task_id, role)` runs the same `FOR UPDATE` on a single row, fails with `task_unavailable` if the row is held by another actor whose claim has not expired. Used by orchestrators that direct work.

### 6.3 Submitting work

Every submission tool takes `(claim_token, payload)` and runs inside a single transaction:
1. `SELECT ... FOR UPDATE` on the row matching `claim_token`.
2. If the row is missing or its `claimed_until < now()`, return `stale_claim` and abort — another actor may now own the row.
3. Validate the proposed state transition via `StateMachineService`.
4. Apply the state change, write the verdict / attempt / finding rows, write the event row, clear or update the claim columns.
5. Commit.
6. After commit, enqueue board sync and Slack notification on the outbox (Section 7.5).

### 6.4 Releasing a claim without submission

`release_claim(claim_token, reason?)` clears the claim columns and writes an event. Used by agents that decide they cannot complete the work and want to surface it back to the pool.

### 6.5 Override path

An actor with `can_override_verdict = true` can call `override_status(entity_type, entity_id, new_status, reason)` which bypasses the claim mechanism. The override writes an `override` event with the reason. This is the only legal way to mutate a row without holding its claim.

### 6.6 Concurrency tests

A required deliverable: `Workstream.ConcurrencyTests/ClaimRaceTests.cs` that:
- Spawns 32 simulated claimers calling `claim_next_task` against a plan seeded with 100 tasks.
- Asserts that every task is claimed exactly once.
- Asserts that no two claimers ever hold the same task simultaneously.
- Asserts that an expired claim is reclaimable by a different actor.
- Asserts that submission with a stale claim token is rejected.

Run this test in CI on every PR. If it ever flakes, the merge is blocked.

**Deliverable for this section**: `Workstream.Data/Repositories/TaskRepository.cs` with the claim queries above, `Workstream.Core/Claims/ClaimService.cs` wrapping the repository, and the concurrency test file.

---

## 7. GitHub Projects (V2) Board Integration

The user's central requirement: tasks move through Backlog → In Progress → Review → Done automatically as their lifecycle changes. This is GitHub Projects **V2**, not the legacy V1 projects, and the API is GraphQL-only.

### 7.1 Why GraphQL, not REST

V2 boards do not exist in the REST API. Every operation — listing items, reading the Status field, updating the Status field option for an item, creating items from issues — goes through `https://api.github.com/graphql`. Use `Octokit.GraphQL.NET` for the typed query builder, falling back to raw GraphQL strings for any mutations the library hasn't caught up to.

### 7.2 Auth

A single GitHub App owns the board access. Install the app on the org. The app has the `projects: read+write`, `issues: read+write`, `contents: read` permissions. The C# server holds the app's private key and mints installation tokens (1-hour TTL) on demand. Store the key as a file mounted into the container, path referenced by env var `WORKSTREAM_GH_APP_KEY_PATH`.

Per-user GitHub auth is **not** used for board operations. The bot acts on behalf of the system; the human's GitHub identity is attribution data (in the assignee field, in commit authorship, in event records) but not the auth principal for board writes. Reason: a developer should not need to grant the MCP server access to their GitHub account; the URL token alone is sufficient identity. This also makes the system robust when an orchestrator agent is the actor.

### 7.3 Item lifecycle

When a plan transitions from `draft` to `active`:
1. For each task in the plan whose `github_board_item_id` is null:
   - Create a draft item on the board with title = task title, body = task description, via `addProjectV2DraftIssue` mutation.
   - Set the Status field to the option corresponding to `tasks.status` per `board_column_mapping`.
   - Set the Assignees field to empty (no assignee yet).
   - Store the returned `PVTI_*` id in `tasks.github_board_item_id`.

When a task is claimed:
1. Update Status → "In Progress" if claim role is the primary execution role for the plan type (e.g. `developer` for dev plans, `auditor` for audit plans). Verifier / reviewer claims do not flip the column.
2. Set Assignees → the claim actor's GitHub username, if that actor has linked their GitHub account.

When a task transitions to `review`: Status → "Review".
When a task transitions to `done`: Status → "Done".
When a task transitions to `blocked` or `needs_human_review`: Status → "Blocked".

### 7.4 Column mapping resolution

The board's actual column names may differ from "Backlog" / "In Progress" / "Review" / "Done". Each `project_boards` row stores the option ids explicitly (`status_option_backlog`, `status_option_in_progress`, ...). The mapping from internal status to option id is:
1. Per-plan override (`plans.config.board_column_overrides`), if set.
2. Per-board mapping (`project_boards` fields), the normal case.
3. Per-plan-type default (`plan_types.state_graph.board_column_mapping`), fallback.

When onboarding a new board, an admin endpoint `POST /admin/boards/discover` takes a project-board node id, queries the Status field options via GraphQL, and presents them for mapping. The admin picks which option is Backlog, In Progress, Review, Done, and optionally Blocked.

### 7.5 Outbox pattern for board updates

Board updates must not block the main MCP transaction. The pattern:
1. The MCP submission commits the state change to Postgres.
2. Immediately after commit, the server writes a row to `board_sync_log` with `result = 'pending'` and `target_column` set.
3. A `BoardSyncWorker` background service (single instance per API replica, leases via `pg_advisory_lock`) polls `board_sync_log` for pending rows and dispatches the GraphQL mutations.
4. On success, mark the row `result = 'success'`, store the response id.
5. On failure, retry with exponential backoff up to 5 attempts, then mark `result = 'failed'` and emit a `board_sync_failed` event.

The outbox makes the system tolerant of GitHub API outages: state in Postgres is always correct, the board catches up when GitHub is reachable. Telemetry (Section 9) exposes the queue depth and the oldest pending sync as a critical health metric.

### 7.6 Inbound: tasks added on the board manually

Some workflow steps will start with a human creating a card on the board ("any task included, update the board directly"). The system needs to ingest those.

A webhook listener at `/webhooks/github/projects` receives `projects_v2_item.created` and `projects_v2_item.edited` events. Verify the webhook signature using the app's webhook secret. On a create event for a board mapped to a project:
1. Resolve the item to a plan: the default is the project's currently-active "drop-in plan" (`projects.config.default_inbound_plan_id`). If unset, the item is parked in a `unsorted_inbound_items` table and surfaced to admin for routing.
2. Create a `tasks` row pointing at the item with status = `pending`, populate from the board item title/body.
3. Write an `event` row of type `task_created_from_board`.

On Status-field edits from the board side, the webhook updates the internal status to keep the board as the leading edge for human-driven moves. Bidirectional sync requires care to avoid loops: every outbound sync stamps a marker in `board_sync_log` for ~10s after the sync; webhook events matching that marker are ignored.

### 7.7 Deliverables

- `Workstream.GitHub/GitHubAppAuthService.cs` — installation token minting.
- `Workstream.GitHub/ProjectsV2Client.cs` — GraphQL operations (CreateDraftItem, UpdateItemStatusField, UpdateItemAssignees, GetBoardFields).
- `Workstream.GitHub/BoardSyncWorker.cs` — outbox processor.
- `Workstream.Api/Webhooks/ProjectsWebhookController.cs` — inbound webhook with signature verification.
- Integration tests against a sandbox org's test board (or against `octokit.fixtures`).

---

## 8. Slack Integration

The user wants a Slack notification on each update. Pattern:

### 8.1 Connection model

One Slack workspace per project (`project_slack`). Could become many later; the schema allows it without migration by promoting `project_slack` to a many-to-many table.

The bot is an internal Slack app installed in each workspace with `chat:write`, `chat:write.public`, `channels:read` scopes. The bot token is stored in HashiCorp Vault, AWS Secrets Manager, or for v1 a plain env-var-mounted secret file referenced by `project_slack.bot_token_secret_ref`. The C# server resolves the reference at use time, never logs the token.

### 8.2 Notification taxonomy

Each project (or plan, overriding the project default) declares which event types trigger a Slack message via the `notify_on` array. The full list:

| Event type | Default for audit | Default for dev | Default message |
|---|---|---|---|
| `task.created` | off | on | "📋 New task: <title>" |
| `task.claimed` | on | on | "👋 @actor claimed <task>" |
| `task.in_progress` | on | on | "🔧 In progress: <task> — @actor" |
| `task.review` | on | on | "👀 In review: <task>" |
| `task.done` | on | on | "✅ Done: <task> (commit <sha>)" |
| `task.blocked` | on | on | "🚧 Blocked: <task> — <reason>" |
| `finding.confirmed` | on | n/a | "🔴 Finding confirmed: <id> <severity>" |
| `finding.rejected` | off | n/a | "⚪ Finding rejected: <id> — <reason>" |
| `fix.confirmed` | on | n/a | "✅ Fix confirmed: <finding-id>" |
| `fix.failed` | on | n/a | "❌ Fix failed: <finding-id> — <reason>" |
| `plan.activated` | on | on | "🚀 Plan <name> active on <project>" |
| `plan.completed` | on | on | "🎉 Plan <name> complete" |

Templates live in `plan_types.config.slack_templates` so per-plan-type customization is possible without code.

### 8.3 Outbox, same pattern as the board

Slack delivery uses the same outbox-and-worker pattern as board sync (Section 7.5). Reliability dominates over latency: a 2-second delay on a Slack post is fine; losing a post because the GitHub API was slow during the same transaction is not.

### 8.4 Threading

Multi-step lifecycles (a task with several finding-and-fix cycles) get threaded: the first message for an entity creates a thread, subsequent messages reply in the thread. The `slack_notify_log` table stores the parent `slack_ts` so threading survives restarts. This keeps the channel readable when many tasks are active.

### 8.5 Deliverables

- `Workstream.Slack/SlackClient.cs` — thin wrapper over SlackNet.
- `Workstream.Slack/NotificationDispatcher.cs` — template resolution + outbox writer.
- `Workstream.Slack/SlackNotifyWorker.cs` — outbox consumer.

---

## 9. MCP Tool Surface

The MCP server exposes tools at `https://mcp.example.com/<token>/mcp` over the Streamable HTTP transport (the current MCP spec; SSE remains supported for backwards compat). The token in the path resolves the calling actor; every tool call inherits that actor context.

Tools cluster into six groups. All tool names are snake_case. All inputs and outputs are JSON. Every mutating tool runs in a single Postgres transaction and writes to the `events` table as part of that transaction.

### 9.1 Discovery and dashboards

**`list_projects()`** → `[{ id, slug, display_name, active_plan_count, my_open_claims }]`. Lists every project visible to the calling actor. All actors see all projects in v1.

**`list_plans(project_id, status?)`** → `[{ id, name, plan_type, status, task_counts: { pending, in_progress, review, done, blocked }, board_url }]`.

**`get_plan_dashboard(plan_id)`** → comprehensive snapshot:
```json
{
  "plan": { "id", "name", "status", "objective" },
  "counts": { "by_status": { ... }, "by_phase": [ { "name", "counts": { ... } } ] },
  "next_claimable": [ { "task_id", "title", "priority", "phase" } ],   // up to 5
  "my_active_claims": [ { "task_id", "role", "claimed_until" } ],
  "stuck_work": [ { "task_id", "since", "status", "claim_holder" } ], // claims older than 2× TTL
  "recent_events": [ ... ]  // last 20
}
```
This is the single call an orchestrator makes to decide what to do next. Result is cached in-process for 5 seconds, invalidated on LISTEN/NOTIFY (`workstream_plan_changed` channel).

**`get_my_active_work()`** → all claims the calling actor currently holds across all plans.

### 9.2 Claim and release

**`claim_next_task(plan_id, role, ttl_override?)`** → `{ task: {...}, claim_token }` or `{ error: "no_work_available" }`. Implements the `FOR UPDATE SKIP LOCKED` query from Section 6.1. The role is validated against the plan type's allowed roles.

**`claim_specific_task(task_id, role)`** → same shape, fails with `task_unavailable` if held.

**`claim_next_finding_for_verification(plan_id)`** → audit-plan-only convenience: claims the highest-severity pending-verification finding.

**`claim_next_finding_for_fix(plan_id)`** → audit-plan-only: claims the highest-severity confirmed finding without active fix.

**`claim_next_attempt_for_review(plan_id)`** → claims the latest attempt awaiting verification (audit) or review (dev).

**`release_claim(claim_token, reason?)`** → clears claim columns, writes a `released` event.

### 9.3 Work submission

Each submission tool atomically: validates the claim token, validates the state transition, writes the result rows, writes the event, releases or transitions the claim. All in one transaction.

**`start_work(claim_token)`** → moves task from `claimed` → `in_progress`, triggers board update to "In Progress".

**`submit_findings(claim_token, findings[])`** — audit-plan only. Each finding gets the structured fields from the master prompt (severity, invariant_impact, symptom, root_cause, repro_steps, adversarial_input, expected, actual, reference_comparison). Returns `{ task_status, finding_ids }`. If `findings` is empty, the task transitions directly to `done`. Otherwise it transitions to `review` (audit) / `in_progress` keeps until findings resolved (varies by plan type).

**`submit_verification_verdict(claim_token, verdict, evidence)`** — audit-plan only. `verdict ∈ { confirmed, rejected, ambiguous }`. `evidence` is structured: `{ pre_output, post_output, adversarial_output, invariant_evidence, reason_category? }`. Writes a `verdicts` row.

**`submit_attempt(claim_token, attempt_data)`** — both plan types. `attempt_data`: `{ files_changed[], approach_summary, side_effects, build_command, test_scenario, diff_ref }`. Auto-increments `attempt_number`. Enforces the retry cap from `plan_types.retry_cap` — submission beyond the cap returns `retry_cap_exceeded` and transitions the entity to `needs_human_review`.

**`submit_attempt_verdict(claim_token, verdict, evidence, commit_hash?)`** — `verdict ∈ { fix_confirmed, fix_failed, partial }` for audit, `{ approved, changes_requested }` for dev. On `fix_confirmed` / `approved`, requires `commit_hash`. On `partial`, the server auto-creates a child finding with external_key `<parent>.partial` and the parent's main fix stays committed.

**`submit_review_decision(claim_token, decision, comments?)`** — dev-plan convenience for the reviewer role, equivalent to `submit_attempt_verdict` with appropriate verdict values.

### 9.4 Lifecycle and admin

**`mark_task_status(task_id, status, reason)`** — used for `deferred`, `blocked`, `skipped`, `out_of_scope`, `needs_human_review`. Requires the relevant permission flag on the calling actor. Bypasses the claim mechanism (it is an override).

**`record_commit(entity_type, entity_id, commit_hash, repo_id)`** — links a commit to a task, finding, or attempt after the fact. Writes a `commit_recorded` event. Optional second arg `target_branch` for context.

**`create_task(plan_id, phase_id?, title, description, paths[]?, priority?, reference_pointer?, depends_on?, notify_slack?=false)`** — admin-tool-grade. Creates a task in `pending` and always triggers board item creation. Slack is opt-in: `notify_slack` defaults to `false`; pass `true` to post a `task.created` message in the project's Slack channel. Returns the new task. Used by orchestrators that decompose larger work units into tasks at runtime.

**`create_tasks(plan_id, tasks[], notify_slack?=false)`** — bulk variant for plan bootstrap (e.g. Phase 0 of an audit). Each item may carry `depends_on_ids` and/or `depends_on_external_keys`; intra-batch keys are resolved on a second pass. Board sync fires for every inserted task. Slack is opt-in (default `false`) to avoid flooding the channel — pass `true` if you want every task announced.

**`override_verdict(entity_type, entity_id, new_status, reason)`** — requires `can_override_verdict`.

### 9.5 Export and forensic

**`export_plan(plan_id, format)`** → `format ∈ { markdown, json }`. Markdown export produces a deterministic snapshot suitable for committing to a repo or copying into chat. JSON export is the full structured tree. Markdown export does **not** feed back in (the database is the source of truth).

**`get_event_log(entity_type, entity_id, since?, limit?)`** → list of events for forensic queries. Used for "show me why this task ended up in needs_human_review" investigations.

**`get_finding(finding_id)`** → the full finding body: severity, invariant impact, symptom, root cause, repro steps, adversarial input, expected/actual, reference comparison, status, and claim holder. The claim tools (`claim_next_finding_for_verification`, `claim_next_finding_for_fix`) return only id/task/status/severity; `get_finding` is how a verifier or fixer recovers the auditor-authored detail it needs to reproduce the issue, including across sessions or after an orchestrator restart. Plain read — no claim required. Returns `not_found` for an unknown id. The live claim token is not returned; use a claim tool to take the finding.

**`get_stuck_work(plan_id?)`** → tasks, findings, attempts past their TTL or stalled in non-terminal states beyond a threshold. Same data as the hourly stuck-work job but on-demand.

### 9.6 Plan and project setup

These are admin tools, available over the same MCP surface but gated by an `is_admin` actor flag (separate from URL-token auth — see Section 14):

**`create_project(slug, display_name, description?)`**
**`add_project_repo(project_id, github_owner, github_repo, is_reference_only?)`**
**`add_project_board(project_id, project_v2_node_id)`** — triggers the discovery flow (Section 7.4).
**`set_project_slack(project_id, workspace_id, channel_id, bot_token_secret_ref)`**
**`create_plan(project_id, plan_type, name, objective, primary_board_id, primary_slack_channel_id?)`**
**`add_phase(plan_id, name, order_index)`**
**`activate_plan(plan_id)`** — moves plan from `draft` → `active`, triggers board item creation for all tasks.

### 9.7 Error model

All tools return `{ ok: true, data: ... }` or `{ ok: false, error: { code, message, details? } }`. Error codes are stable, documented strings:

| Code | Meaning |
|---|---|
| `unauthorized` | URL token invalid or revoked |
| `not_found` | Entity does not exist |
| `stale_claim` | Claim token is expired or does not match the entity |
| `illegal_transition` | State machine forbids this transition |
| `permission_denied` | Actor lacks required permission flag |
| `retry_cap_exceeded` | Attempt count exceeded plan's retry cap |
| `validation_error` | Input shape invalid |
| `conflict` | Concurrent modification detected |
| `external_dependency_error` | GitHub or Slack call failed (not surfaced from sync flows; outbox swallows these) |
| `service_unavailable` | DB unreachable or in degraded mode |

The `details` object on `illegal_transition` includes `allowed_next: string[]` so the caller (especially an LLM) can correct course without a re-read of the spec.

### 9.8 Deliverable

`Workstream.Mcp/Tools/*.cs` — one file per tool group. Each tool is a class implementing `IMcpTool` with `Name`, `Description`, `InputSchema`, `Execute(input, context)`. Schema is JSON Schema. Descriptions are deliberately verbose — they are read by the LLM, so each tool's description includes when to use it, what state the entity must be in, and what the return shape means.

---

## 10. Configuration Model

The user asked specifically for: configuration for users/repos/boards, configuration for MCP URLs/unique ids. The data model in Section 4 already covers this; this section specifies the operational surface.

### 10.1 Admin CLI

A companion tool `workstream-admin` (in `tools/workstream-admin/`) is a .NET console app that calls the admin endpoints. Used by the system operator to bootstrap and maintain configuration. Why a CLI: faster than a web UI to build, gives reproducible scripts for environment recreation.

Commands:
```
workstream-admin user create --github-username alice --display "Alice" --type human
workstream-admin user create --github-username example-orchestrator --type orchestrator
workstream-admin user rotate-token --github-username alice
workstream-admin user grant --github-username alice --permission can_override_verdict

workstream-admin project create --slug example-app --name "Example App"
workstream-admin project add-repo --slug example-app --owner example-org --repo example-app
workstream-admin project add-reference-repo --slug example-app --owner example-org --repo example-reference
workstream-admin project add-board --slug example-app --project-v2-node-id PVT_xxx
workstream-admin project set-slack --slug example-app --workspace TXXXX --channel CXXXX

workstream-admin plan create --project example-app --type audit --name "Phase 2 Audit"
workstream-admin plan activate --id <plan-id>

workstream-admin plan-type list
workstream-admin plan-type show audit
workstream-admin plan-type update audit --from-file profiles/audit.json
```

The CLI reads `WORKSTREAM_ADMIN_URL` and `WORKSTREAM_ADMIN_TOKEN` from env / `.workstream-admin.toml`. All commands print the request/response for auditability.

### 10.2 Declarative bootstrap

A YAML file `deploy/bootstrap.yml` defines the initial state of the system. The admin CLI command `workstream-admin apply bootstrap.yml` is idempotent: it diffs the YAML against the DB and applies only what changed. Example:

```yaml
plan_types:
  audit: { file: profiles/audit.json }
  development: { file: profiles/development.json }

users:
  - github_username: alice
    display_name: Alice
    type: human
    permissions: [can_override_verdict, can_archive_plan, can_mark_needs_human_review]
  - github_username: example-orchestrator
    type: orchestrator

projects:
  - slug: example-app
    display_name: Example App
    repos:
      - { owner: example-org, repo: example-app }
      - { owner: example-org, repo: example-reference, reference_only: true }
    boards:
      - project_v2_node_id: PVT_xxx
        column_mapping:
          backlog: Backlog
          in_progress: "In Progress"
          review: Review
          done: Done
          blocked: Blocked
    slack:
      workspace_id: TXXXX
      channel_id: CXXXX
      bot_token_secret_ref: vault://workstream/slack/example-app
  - slug: billing-service
    # ...
```

This file is the canonical operational artifact. Commit it to the workstream-mcp repo (with secrets externalized). Re-running `apply` after a manual change re-asserts the desired state — useful for drift detection.

### 10.3 URL surface

The user-facing URL format `mcp.example.com/<token>` is constructed at user-creation time. The base host (`mcp.example.com`) is configured via `WORKSTREAM_PUBLIC_BASE_URL`. Token generation, storage, and rotation are described in Section 3.

For the curious: the actual MCP endpoint inside the host is the `/mcp` suffix per Streamable HTTP convention. The user's full Claude config URL is `https://mcp.example.com/<token>/mcp`. The bare `/<token>` returns a small JSON status page that confirms the token resolves to a valid user and shows the user's display name — useful for sanity-checking a copy-pasted URL before adding it to Claude.

---

## 11. Telemetry and Resilience

The user called this out as critical: "telemetry for the workflow is important as it is the core workflow and must be resilient." Three telemetry surfaces, three resilience postures.

### 11.1 The events table is the business audit log

Every state transition writes an event row inside the same transaction. This is forensic gold: "show me every fix that failed last week and what the failure reason was" is `SELECT ... FROM events WHERE event_type = 'verdict_submitted' AND payload->>'verdict' = 'fix_failed' AND at > now() - interval '7 days'`.

The events table grows. At ~1000 events/day per active plan it is fine for years. Partition by month after the table exceeds 100M rows (years away).

### 11.2 OpenTelemetry for operational telemetry

Wire OTel from day one. The exporter target is configurable (`WORKSTREAM_OTEL_ENDPOINT`); in dev it points to a local Tempo / Loki / Prometheus stack via OTLP. Three signal types:

**Traces**: every MCP tool call is a span. Inside it: DB query spans (Npgsql instrumentation is auto), GitHub API spans, Slack API spans. Trace ID is propagated into the `events.payload` so business events can be joined to operational traces.

**Metrics** (Prometheus-style names):
- `workstream_mcp_tool_duration_seconds{tool,result}` histogram
- `workstream_claim_contention_ratio` — fraction of `claim_next_task` calls that returned zero rows / total calls (high values mean too many idle agents polling)
- `workstream_claim_ttl_expirations_total{role}` counter — how often crashed agents are leaving stale claims
- `workstream_board_sync_queue_depth` gauge
- `workstream_board_sync_oldest_pending_seconds` gauge — paging alert above 5 minutes
- `workstream_slack_notify_queue_depth` gauge
- `workstream_db_pool_in_use` gauge
- `workstream_db_pool_waiting` gauge — paging alert if non-zero for sustained periods
- `workstream_dashboard_cache_hit_ratio`
- `workstream_state_transitions_total{plan_type,from,to}` counter
- `workstream_token_resolution_failures_total{reason}` counter

**Logs**: structured JSON via Serilog. Each log line carries `actor_id`, `plan_id`, `task_id`, `trace_id`. **Never** log: URL tokens, claim tokens, Slack bot tokens, GitHub installation tokens, secret refs.

### 11.3 Hourly stuck-work report

A background job runs hourly, computes:
- Tasks in `in_progress` longer than 2× the role's TTL.
- Findings in `pending_verification` for more than 24h.
- Attempts past their TTL without a verdict.
- Plans with no progress in the last 24h (no events).

Results are written to a small `stuck_work_reports` table and posted to a `#workstream-ops` Slack channel. This catches silent failure modes: orchestrators that stopped polling, agents stuck in retry loops, plans that nobody is working on.

### 11.4 Resilience postures

| Failure mode | Frequency | Response |
|---|---|---|
| Agent dies mid-task | Multiple times daily | Claim TTL expires, work reclaimed automatically. No human action. |
| Malformed submission | Common | Transaction aborts atomically, claim preserved, retried or expired. |
| Claim race | Constant under load | `FOR UPDATE SKIP LOCKED` resolves cleanly. Metric tracks contention. |
| DB pool exhausted | Rare | Tools return `service_unavailable`. Agents back off. Alert on pool wait time. |
| Postgres down | Rare | All tools return `service_unavailable`. Agents back off. No data loss. |
| GitHub API down | Occasional | Outbox queues sync writes. DB state stays correct. Board catches up after recovery. |
| Slack API down | Occasional | Same outbox pattern. |
| MCP server bug introduces bad state | Possible | Events table allows replay. Admin tool writes corrective event. |
| Orchestrator hallucinates contradictory verdict | Possible | State machine rejects illegal transitions; LLM sees structured error with allowed next states. |

### 11.5 Backups

`pg_dump` runs nightly to a separate volume on the host. Weekly copy to off-host storage. Restore drill quarterly: stand up a new pg instance, restore the latest dump, verify the system passes its smoke tests. The events table is part of the dump (it is the audit trail of the audit) and its durability matters as much as the primary state.

### 11.6 Deliverables

- `Workstream.Telemetry/TelemetryBootstrap.cs` — wires OTel resources, propagators, exporters.
- `Workstream.Telemetry/Metrics.cs` — strongly-typed metric registrations.
- `Workstream.Core/StuckWork/StuckWorkJob.cs` — background hosted service.
- A Grafana dashboard JSON committed to `deploy/grafana/workstream.json`.
- An alerting rules file `deploy/prometheus/workstream-alerts.yml` covering the paging thresholds above.

---

## 12. Deployment Topology

The user specified: two containers, Postgres + API. Hold that line.

### 12.1 docker-compose.yml

```yaml
version: "3.9"
services:
  postgres:
    image: postgres:16-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: workstream
      POSTGRES_USER: workstream
      POSTGRES_PASSWORD_FILE: /run/secrets/pg_password
    volumes:
      - workstream-pg-data:/var/lib/postgresql/data
      - ./deploy/postgres/postgresql.conf:/etc/postgresql/postgresql.conf
    secrets:
      - pg_password
    command: postgres -c config_file=/etc/postgresql/postgresql.conf
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U workstream"]
      interval: 5s
      timeout: 5s
      retries: 10

  api:
    build:
      context: .
      dockerfile: deploy/Dockerfile.api
    restart: unless-stopped
    depends_on:
      postgres:
        condition: service_healthy
    environment:
      ASPNETCORE_URLS: http://0.0.0.0:8080
      WORKSTREAM_PUBLIC_BASE_URL: https://mcp.example.com
      WORKSTREAM_DB_CONNECTION: "Host=postgres;Database=workstream;Username=workstream;Password=__from_secret__;Maximum Pool Size=50;Pooling=true"
      WORKSTREAM_DB_PASSWORD_FILE: /run/secrets/pg_password
      WORKSTREAM_ADMIN_TOKEN_FILE: /run/secrets/admin_token
      WORKSTREAM_GH_APP_ID: "${WORKSTREAM_GH_APP_ID}"
      WORKSTREAM_GH_APP_KEY_PATH: /run/secrets/gh_app_key
      WORKSTREAM_GH_WEBHOOK_SECRET_FILE: /run/secrets/gh_webhook_secret
      WORKSTREAM_OTEL_ENDPOINT: "${WORKSTREAM_OTEL_ENDPOINT:-http://otel-collector:4317}"
    secrets:
      - pg_password
      - admin_token
      - gh_app_key
      - gh_webhook_secret
    ports:
      - "8080:8080"

secrets:
  pg_password:    { file: ./secrets/pg_password }
  admin_token:    { file: ./secrets/admin_token }
  gh_app_key:     { file: ./secrets/gh_app_key.pem }
  gh_webhook_secret: { file: ./secrets/gh_webhook_secret }

volumes:
  workstream-pg-data:
```

The Slack bot tokens for each project are mounted as additional secrets, referenced by `bot_token_secret_ref` columns. They live in `./secrets/slack-<project-slug>` files. Adding a project means dropping a file and updating compose; for v1 this is acceptable.

### 12.2 Decision: no PgBouncer at v1

An earlier design called for PgBouncer. For this v1 with .NET 10 and Npgsql's built-in pooling, **PgBouncer is not needed** at the expected scale (low double-digit concurrent agents). Npgsql's pooler is mature and the transaction patterns here are short-lived. **Revisit** PgBouncer when concurrent agent count exceeds 50 or DB pool waits become non-trivial. Add it as a third container at that point without schema change.

### 12.3 Reverse proxy and TLS

The host runs Caddy (or an existing nginx) terminating TLS for `mcp.example.com` and proxying to the `api` container on port 8080. Caddy config:
```caddyfile
mcp.example.com {
  reverse_proxy api:8080
  log {
    format json
    output file /var/log/caddy/workstream.log
  }
}
```
TLS via Let's Encrypt, automatic via Caddy. Important: configure Caddy access logs to **omit the path** (or redact the second path segment), since the URL token is in the path and shows up in default logs otherwise. See Section 14.

### 12.4 Deployment

The deployment flow:
1. Build the API image locally with `docker build -t workstream-mcp:<sha> -f deploy/Dockerfile.api .`.
2. Push to the registry the host can read (ghcr.io with the GH App credentials, or a private registry).
3. Over SSH, pull and restart on the host with `docker compose pull && docker compose up -d`.
4. Run migrations on the host: `docker compose exec api dotnet Workstream.Migrations.dll up`.

A `deploy/migrate.sh` script encapsulates step 4 and is what CI runs after a successful container deploy.

---

## 13. Security Hardening

The URL-as-credential model needs operational discipline to be secure. This section specifies it.

### 13.1 Token handling

- **Never log the path**. Caddy is configured with `request> uri redact` or its equivalent — the structured log format omits the path or substitutes `<redacted>` for the second segment.
- **Never log the token**. The MCP server's logging middleware strips the path before any log line is written; only the resolved `actor_id` reaches the logs.
- **Never include the token in error messages**, error pages, or trace exporters. OTel attribute `http.url` is replaced with the user-id-templated form `https://mcp.example.com/<user:alice>/mcp`.
- **Never put the token in webhook responses or any outbound HTTP** the server makes (e.g. GitHub callbacks).

### 13.2 Token transport hardening

- The path token is only valid over HTTPS. Caddy rejects HTTP. Set HSTS with a long max-age and `includeSubDomains`.
- The server sets `Referrer-Policy: no-referrer` on all responses, so a token cannot leak via referrer if a Claude session ever follows a link.
- Cookies are not used. The system is stateless from the HTTP perspective.

### 13.3 Rate limiting

Per-token rate limit, applied at the API layer (not Caddy, so we have access to the resolved actor):
- 60 tool calls per minute per actor (sliding window).
- 600 tool calls per hour per actor.
- Burst of 30.

Limits are deliberately generous because legitimate orchestrators can make many rapid calls during plan setup. Tuneable per actor type via `users.config.rate_limit_overrides`.

### 13.4 Webhook security

- `/webhooks/github/projects` verifies the `X-Hub-Signature-256` header against the configured app webhook secret.
- Replay protection: each webhook delivery's `X-GitHub-Delivery` GUID is recorded in a `webhook_deliveries` table and re-delivery is ignored.
- Webhook requests do not have an actor; they synthesize a system actor (`actor_type = orchestrator`, `github_username = workstream-bot`) for event attribution.

### 13.5 Prohibited operations

The MCP server **does not**:
- Run arbitrary code. There is no execution endpoint, no shell pass-through.
- Mutate GitHub repo contents (no commits, no file writes, no PR creation). Only Project board items and item-status changes.
- Mutate Slack beyond posting messages and replies.
- Read git history. The server is workflow state, not version control.

This bounded surface is itself a security feature: a compromised URL token unlocks read/write to plan state, board state, and Slack posting on the project's channel, nothing more.

### 13.6 Deliverables

- `Workstream.Api/Middleware/TokenResolutionMiddleware.cs`
- `Workstream.Api/Middleware/RateLimitMiddleware.cs`
- `Workstream.Api/Middleware/RequestLoggingMiddleware.cs` (with explicit path/token redaction)
- `Workstream.Api/Webhooks/SignatureVerification.cs`
- Caddy config snippet `deploy/Caddyfile`.

---

## 14. Plug-and-Play Onboarding (the headline workflow)

This is the user's stated end-goal: "plug and play for a new developer just to add this mcp, picking tasks, start development, update the board with the in progress tasks, and finish the tasks and update the board accordingly and update the slack channel."

### 14.1 Operator side (once per developer)

```bash
workstream-admin user create --github-username new-dev --display "New Developer" --type human
# Output: User created. URL: https://mcp.example.com/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB
# Copy this URL exactly. It will not be shown again.
```

### 14.2 Developer side (one-time setup)

Add to Claude config (`~/.claude/mcp.json` or the equivalent Claude UI):
```json
{
  "mcpServers": {
    "workstream": {
      "url": "https://mcp.example.com/Yk8j2_aBcDeFgHiJkLmNoPqRsTuVwXyZ1234567890aB/mcp",
      "transport": "http"
    }
  }
}
```
Done. The developer never sees a token prompt, never copies a GitHub PAT, never logs into anything.

### 14.3 Daily workflow

The developer says to Claude: "What can I pick up on the example-app dev plan?"

Behind the scenes:
1. Claude calls `list_plans({ project_id: <example-app> })` → sees dev plan id.
2. Claude calls `get_plan_dashboard(<dev_plan_id>)` → next 5 claimable tasks.
3. Claude presents them, developer says "let's do the second one."
4. Claude calls `claim_specific_task(<task_id>, "developer")`.
5. Server: writes claim, posts `task.claimed` to Slack, moves the board item to "In Progress", returns the claim token.
6. Claude calls `start_work(<claim_token>)` (the claim already put it `claimed`; `start_work` moves to `in_progress` proper and triggers the board column flip if it wasn't already).
7. Developer codes. Claude assists. When ready: "submit this for review."
8. Claude calls `submit_attempt(<claim_token>, { files_changed, approach, build_command, test_scenario, diff_ref: "commit:abc123" })`.
9. Server transitions task to `review`, posts Slack, moves board to "Review".
10. Reviewer (different actor, same MCP URL pattern) gets notified, claims the review, submits `approved` verdict.
11. Server transitions task to `done`, records commit hash, posts Slack, moves board to "Done".

Every step writes to `events`, every external sync goes through the outbox. If the developer's Claude session dies between steps 5 and 8, the claim expires after 4 hours and the task is reclaimable. The work artifacts (commits, files) survive because they live in git, not in the MCP server.

### 14.4 Audit workflow (same surface, different roles)

The same plug-and-play applies to audit work. An orchestrator agent's actor record has `type = orchestrator`, the same URL pattern, and calls the audit-specific tools (`submit_findings`, `submit_verification_verdict`, etc.) instead of dev-specific ones. The plan profile is `audit` instead of `development`. The board transitions and Slack notifications follow the same rules but with audit-specific event types.

### 14.5 Deliverable for this section

A `docs/onboarding.md` file with:
- The two operator commands above.
- The exact Claude config snippet.
- A short script (`scripts/verify-mcp.sh`) the developer can run to hit the bare `/<token>` endpoint and confirm the URL works before adding it to Claude.

---

## 15. Orchestrator Prompts (out-of-band, but specified here)

The user noted: "we will work on orchestrator prompt on the server, md files to run the agents, parallelism, etc., other than the mcp server." This section sketches that interface so the MCP design supports it cleanly.

### 15.1 Where orchestrator prompts live

`plan_types.prompt_template_ref` points to a markdown file in a known repo (e.g. `workstream-prompts/audit-orchestrator.md`). The MCP server itself does not execute the prompt; it just stores the reference so any orchestrator session can fetch it.

### 15.2 Orchestrator-side loop shape

An orchestrator session pseudocode, regardless of plan type:

```
loop:
  dashboard = get_plan_dashboard(plan_id)
  if dashboard.next_claimable.empty and dashboard.my_active_claims.empty:
    sleep 30s; continue

  for task in dashboard.next_claimable:
    spawn subagent (role: primary_role_for_plan_type) with:
      - the orchestrator prompt template
      - the task fields
      - the claim token (after orchestrator calls claim_specific_task on its behalf)
    subagent runs to completion or TTL
    on success: subagent calls the appropriate submission tool with its claim token
    on failure: claim expires, task returns to pool

  for finding in dashboard.findings_needing_verification:
    spawn verifier subagent ...

  for finding in dashboard.findings_needing_fix:
    spawn fixer subagent ...
```

The MCP server supports this without further work: `claim_specific_task` is the explicit-claim tool, `get_plan_dashboard` is the work-discovery tool, parallelism is bounded by how many subagents the orchestrator spawns (and the natural concurrency limit of the claim mechanism — never more than one agent on the same task).

### 15.3 Markdown prompts are the agent contracts

The audit master prompt provided as input becomes the seed for `workstream-prompts/audit-orchestrator.md`. Generalize the language:
- "auditingplan.md" → "the audit plan, accessed via Workstream MCP tools"
- Per-feature loop logic stays but the markdown updates are replaced with MCP tool calls (`submit_findings`, `submit_verification_verdict`, ...).
- The non-negotiable invariants (compression preserved, etc.) stay specific to the project — they live in `projects.config.invariants` JSONB and the orchestrator prompt template references them.

### 15.4 Not in scope for the MCP server build

Building the orchestrator runtime, the markdown templating, the subagent dispatcher — none of this is the MCP server's job. The MCP server is the state-of-record. The orchestrator is a separate concern, built next, against the tool surface in Section 9.

---

## 16. Testing Strategy

Three test tiers, all in CI.

### 16.1 Unit tests (`Workstream.UnitTests`)

- State machine: exhaustive legal/illegal transition tests for both seed plan profiles.
- Token generation: entropy is correct, regeneration produces a different token, hash is consistent.
- Tool input validation: every tool has schema-violation tests.

### 16.2 Integration tests (`Workstream.IntegrationTests`)

Use Testcontainers-Postgres. Each test class spins up a clean Postgres, runs migrations, exercises end-to-end flows:
- Create project, repo, board (mocked GitHub), plan, tasks. Claim, start work, submit, verify board sync log entries.
- Webhook ingestion creates tasks correctly.
- Outbox retries on simulated GitHub failures.
- Slack outbox the same.
- Rate limit enforcement (mocked clock).

### 16.3 Concurrency tests (`Workstream.ConcurrencyTests`)

Specified in Section 6.6 already. These are the load-bearing tests for correctness under contention.

### 16.4 Smoke test against a real deployment

A `scripts/smoke.sh` runs through the plug-and-play workflow against a deployed staging environment. Used as the post-deploy gate.

### 16.5 Deliverables

The three test projects, the smoke script, and a CI config (`.github/workflows/ci.yml`) running all three tiers on every PR plus the smoke script on every push to main.

---

## 17. What is Explicitly Out of Scope for v1

To stay honest about complexity boundaries:

- **Web UI**. The MCP tool surface and the admin CLI are the interface. A future React dashboard reads the same events table.
- **PgBouncer**. Not needed at v1 scale. Add when needed; see Section 12.2.
- **Real-time push to clients beyond Slack**. The dashboard is poll-based with caching.
- **GitHub Issues sync**. The system writes to the Project V2 board only. Linking an issue to a task is one-way: the task knows about the issue if it was created from one, but the system does not write back to issues. Can be added; not v1.
- **Cross-project analytics** ("which file has the most findings across all projects"). Schema supports it; queries and any UI come later.
- **Per-user GitHub OAuth**. URL-token is the v1 auth model. OAuth is the v2 path if threat model expands.
- **Multi-region or HA Postgres**. A single instance is sufficient.
- **RBAC beyond the small flag set**. The `users.can_*` flags are enough for v1.
- **Automatic SLA escalation**. The stuck-work job surfaces issues to Slack; it does not auto-reassign.
- **Plan-type editor UI**. Plan types are edited via the admin CLI's JSON-file path. A future UI is straightforward.
- **Self-service user signup**. All users are provisioned by an operator.

---

## 18. Build Sequencing

Roughly two to three weeks of focused engineering for v1. The order matters because earlier steps are foundations for later ones.

**Week 1: foundation**

1. Repo scaffold (Section 1). Empty projects, package versions pinned, CI skeleton.
2. Schema and migrations (Section 4). The full DDL, all check constraints, all indexes, the triggers. This is the single most expensive thing to change later; get it right.
3. The two seed plan-type profiles (Section 5.2) committed as migration files.
4. `ClaimService` and the claim queries (Section 6). The `FOR UPDATE SKIP LOCKED` patterns. The concurrency test suite (Section 6.6) running in CI from day one.
5. `StateMachineService` (Section 5.3) with exhaustive legal-and-illegal transition tests.

**Week 2: tools and integration**

6. The MCP tool surface (Section 9). The full list, each with input schema, output schema, transaction wrapper, event emission. Each tool gets an integration test.
7. URL-token middleware (Sections 3 and 13.1). Token resolution, redacted logging, rate limiting.
8. The MCP transport itself: Streamable HTTP host, tool registration, schema export.
9. The GitHub Projects V2 client (Section 7.1). The four mutations (CreateDraftItem, UpdateItemStatus, UpdateItemAssignees, GetBoardFields). The auth via GitHub App.
10. The board sync outbox and worker (Section 7.5).
11. The Slack client and notification dispatcher (Section 8) with the outbox.
12. Webhook ingestion (Section 7.6) for board changes coming in from GitHub.

**Week 3: operations, observability, polish**

13. The admin CLI `workstream-admin` (Section 10.1).
14. The bootstrap.yml apply flow (Section 10.2).
15. OTel wiring, metrics, structured logging (Section 11).
16. Stuck-work job and report (Section 11.3).
17. Deployment artifacts: Dockerfile, compose, Caddyfile, migrate script (Section 12).
18. Smoke test script (Section 16.4).
19. Deploy to a staging host.
20. Create the first real project, the first real plan, one test user, walk through the plug-and-play flow end to end.

By the end of week three, a developer or orchestrator can add their MCP URL to Claude and start consuming work. Everything beyond that is polish, additional plan types, and the orchestrator runtime that lives outside this server.

---

## 19. Specific Acceptance Criteria

Implementation is considered complete when all of the following hold:

1. **Multi-tenancy works**: two users with two different URL tokens can independently call `list_projects`, `claim_next_task`, and `submit_*` against the same plan, never see each other's claim tokens, and never collide.

2. **Multi-project, multi-repo, multi-board**: a single deployed instance hosts at least two projects (e.g. `example-app` and `billing-service`), each with its own repos, boards, and Slack channel. A user with a single MCP URL can act on both.

3. **Board sync works in both directions**: an internal status change moves a card on the right board within 10 seconds; a manual board move ingests within 10 seconds via webhook; bidirectional syncing does not loop.

4. **Slack notifications work**: every event type in the `notify_on` array produces a Slack message in the right channel within 10 seconds. Threading works across the lifecycle of a single task.

5. **Concurrency tests pass**: 32 simulated claimers, 100 tasks, zero duplicate claims, zero deadlocks, ten runs in a row in CI.

6. **TTL recovery works**: an integration test that claims a task, hard-aborts the process, and re-claims after TTL expiry succeeds without manual intervention.

7. **State machine enforced**: every illegal transition the two seed profiles forbid returns a structured `illegal_transition` error with a populated `allowed_next` list.

8. **Telemetry visible**: hitting a staging deployment with a small workload produces traces in the OTel backend, metrics in Prometheus, and a populated Grafana dashboard.

9. **The plug-and-play story works**: a fresh developer, given only their MCP URL, can claim a task, do the work, submit it, and see the board update and the Slack notification, without any other configuration.

10. **The audit story works**: an orchestrator agent, given only its MCP URL, can run the full per-feature loop from an audit orchestrator prompt (see `orchestrators/example/`) — claim feature, spawn auditor, ingest findings, spawn verifier, ingest verdicts, spawn fixer, ingest attempts, spawn fix-verifier, ingest fix verdict, commit, move on — entirely through MCP tool calls, with no markdown plan file involved.

If any of these ten do not hold, v1 is not done.

---

## 20. Closing Notes for the Build Session

A few things to internalize before writing code:

**The claim primitive is the system.** Almost every interesting behavior derives from it. If `claim_next_task` is wrong, everything downstream is wrong. Spend the time on Section 6 and the concurrency tests.

**The state machine is data.** Resist hardcoding the audit profile's transitions in C#. The whole reason for the data-driven design is that the next plan type (testing? migration? release management?) should not require a recompile.

**Outboxes everywhere.** Any time the MCP server has to talk to an external system (GitHub, Slack), the pattern is: commit Postgres state, write outbox row, return success to the caller, let a worker drain the outbox with retries. Never put external API calls inside the request transaction.

**The events table is sacred.** It is the only forensic record of what actually happened. Write to it religiously inside every state-mutating transaction. Treat it as append-only at the application layer too — never delete rows even when "cleaning up."

**The URL token is a credential.** Treat it with the discipline of a password: redact in logs, redact in traces, redact in error messages, never echo back. The plug-and-play story collapses the moment a token leaks to a public bucket.

**Generality is the requirement.** Audit is the first plan type, not the only one. Every design decision that mentions "auditor" or "finding" should have an answer for "what does this look like for a dev plan, a test plan, a migration plan?" Section 5's data-driven state graph is the mechanism; respect it everywhere.

**Two containers, no more.** Postgres + API. The temptation to add Redis for caching, a queue for outboxes, a separate event store for events, will be strong. Resist. The in-process cache plus LISTEN/NOTIFY plus a Postgres-backed outbox is enough for the load you actually have. Add infrastructure when you can point at a metric showing you need it, not before.

Build it. Ship it. Connect one Claude session, then ten. Watch it run. The next document is the orchestrator-prompt spec that sits on top of this surface.

