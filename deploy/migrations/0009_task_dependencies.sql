-- Task dependency edges. Lets the orchestrator declare "task B can't run until task
-- A is terminal" so claim_next_task / get_plan_dashboard skip blocked work and
-- distribute parallel-safe tasks to subagents on git worktrees.
--
-- Same-plan-ness and cycle-freeness are enforced at the application layer
-- (CreateTaskTool runs a recursive CTE check inside the insert transaction) —
-- neither is expressible as a simple CHECK.

BEGIN;

CREATE TABLE task_dependencies (
    task_id            uuid NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    depends_on_task_id uuid NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    created_at         timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (task_id, depends_on_task_id),
    CONSTRAINT task_dependencies_no_self_loop CHECK (task_id <> depends_on_task_id)
);

CREATE INDEX idx_task_dependencies_depends_on ON task_dependencies (depends_on_task_id);

INSERT INTO __migrations (version) VALUES ('0009_task_dependencies')
ON CONFLICT (version) DO NOTHING;

COMMIT;
