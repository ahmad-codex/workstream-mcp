# State machine

State transitions are defined in `plan_types.state_graph` JSONB. There is no hardcoded transition logic in C#; everything routes through `StateMachineService.ValidateTransition` and the rules live in the database (§5).

## Why data-driven

Adding a new plan type later (`testing`, `migration`, `release-management`, …) means inserting one row into `plan_types` plus a JSON file under `deploy/profiles/`. The C# code does not change. This was an explicit non-negotiable from the spec.

## Shape of the graph

```jsonc
{
  "task_states":       ["pending","claimed","in_progress","review","done","deferred","blocked","needs_human_review","skipped","out_of_scope"],
  "task_initial":      "pending",
  "task_terminal":     ["done","deferred","needs_human_review","skipped","out_of_scope"],
  "task_transitions":  [
    { "from": "pending",     "to": "claimed",     "via": "claim_task",  "requires_role": ["developer"] },
    { "from": "claimed",     "to": "in_progress", "via": "start_work" },
    { "from": "*",           "to": "needs_human_review", "via": "escalate" }
    // …
  ],
  "finding_states":    [...],
  "finding_initial":   "pending_verification",
  "finding_terminal":  [...],
  "finding_transitions": [...],
  "board_column_mapping": { "pending": "backlog", "in_progress": "in_progress", ... }
}
```

Rules can specify:
- `from` / `to` / `via` — required. `from: "*"` is a wildcard.
- `verdict` — when multiple rules share a `from/to/via`, the verdict disambiguates (e.g. `submit_review_decision` with `verdict=approved` vs `verdict=changes_requested`).
- `requires_role` — array of claim roles allowed to execute the transition.
- `requires_permission` — actor-level permission flag (`can_override_verdict`, etc.).
- `guard` — named predicate evaluated against `TransitionContext`. Known guards: `findings_empty`, `findings_non_empty`, `below_retry_cap`, `at_retry_cap`, `all_findings_terminal_resolved`.

## Seed profiles

Two profiles ship in `deploy/profiles/`:

| Profile | Roles | TTLs | Findings | Retry cap |
|---|---|---|---|---|
| `audit` | `auditor`, `verifier`, `fixer`, `fix_verifier` | 30m / 15m / 45m / 15m | yes | 3 |
| `development` | `developer`, `reviewer` | 4h / 1h | no | 3 (review rounds) |

The full state graphs are in [`audit.json`](../deploy/profiles/audit.json) and [`development.json`](../deploy/profiles/development.json). They are re-embedded as JSONB literals in [`0002_seed_plan_types.sql`](../deploy/migrations/0002_seed_plan_types.sql); the two must stay in sync.

## Validation flow

Every mutating tool calls `_sm.ValidateTransition(graph, entityType, from, to, via, actor, claimRole, context)` before applying the change. On illegal transitions the result includes `allowed_next: string[]` so an LLM caller can self-correct without re-reading the spec:

```json
{
  "ok": false,
  "error": {
    "code": "illegal_transition",
    "message": "transition pending -> done is not legal",
    "details": { "from": "pending", "to": "done", "allowed_next": ["claimed", "needs_human_review", ...] }
  }
}
```

## Adding a new plan type

1. Drop a JSON profile under `deploy/profiles/<your-type>.json` following the audit / development shape.
2. Add a migration `deploy/migrations/000N_seed_<your-type>.sql` that `INSERT`s one row into `plan_types`.
3. Add unit tests in `Workstream.UnitTests` covering legal and illegal transitions for the new profile (mirror `StateMachineServiceTests`).
4. **Do not** touch C# tool code. If you find yourself wanting to, the design is degrading — extend the state-graph schema instead.

## Where the code lives

- `src/Workstream.Core/StateMachine/StateGraph.cs` — record types.
- `src/Workstream.Core/StateMachine/StateGraphParser.cs` — JSONB → records.
- `src/Workstream.Core/StateMachine/StateMachineService.cs` — `ValidateTransition`.
- `tests/Workstream.UnitTests/StateMachineServiceTests.cs` — exhaustive transition tests against both seed profiles.
