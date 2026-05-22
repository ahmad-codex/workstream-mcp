-- Per-plan GitHub milestone. request_audit's plan gets a repo milestone; its tasks are
-- created as real issues assigned to that milestone (not Project V2 draft items). The
-- milestone is closed when the plan is archived.
--
-- plans.github_milestone_number — the milestone's number in the project's primary repo.
-- milestone_sync_log — outbox for the two external milestone operations (create on plan
-- creation, close on archive); drained by BoardSyncWorker like board_sync_log.

BEGIN;

ALTER TABLE plans
    ADD COLUMN IF NOT EXISTS github_milestone_number integer;

CREATE TABLE IF NOT EXISTS milestone_sync_log (
    id                bigserial PRIMARY KEY,
    plan_id           uuid NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    action            text NOT NULL,                 -- 'create' | 'close'
    outcome           text,                          -- 'Completed' | 'Canceled' (close only)
    reason            text,
    attempts          integer NOT NULL DEFAULT 0,
    next_attempt_at   timestamptz NOT NULL DEFAULT now(),
    last_attempted_at timestamptz,
    result            text NOT NULL DEFAULT 'pending',
    error             text,
    created_at        timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_milestone_sync_claimable
    ON milestone_sync_log (next_attempt_at)
    WHERE result IN ('pending', 'retry');

INSERT INTO __migrations (version) VALUES ('0020_audit_milestone')
ON CONFLICT (version) DO NOTHING;

COMMIT;
