-- Audit-dispatch outbox. The request_audit MCP tool writes one row per project; the
-- AuditDispatchWorker drains the queue and launches that project's audit orchestrator
-- (e.g. `claude -p /audit-run` headless in the project's repo). Same outbox pattern as
-- board_sync_log / slack_notify_log: the MCP call only commits Postgres state, the
-- background worker does the slow external work with retries.
--
-- requested_by is recorded for forensics only — no FK, so a deleted actor never blocks
-- the audit history.

BEGIN;

CREATE TABLE IF NOT EXISTS audit_dispatch_log (
    id                bigserial PRIMARY KEY,
    project_id        uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    requested_by      uuid,
    status            text NOT NULL DEFAULT 'pending',   -- pending|running|success|retry|failed
    attempts          integer NOT NULL DEFAULT 0,
    next_attempt_at   timestamptz NOT NULL DEFAULT now(),
    last_attempted_at timestamptz,
    result            text,                              -- short success summary
    error             text,
    created_at        timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS ix_audit_dispatch_claimable
    ON audit_dispatch_log (next_attempt_at)
    WHERE status IN ('pending', 'retry');

INSERT INTO __migrations (version) VALUES ('0018_audit_dispatch_log')
ON CONFLICT (version) DO NOTHING;

COMMIT;
