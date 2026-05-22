-- Distinguish audit-dispatch actions: 'run' launches a project's audit orchestrator,
-- 'cancel' signals a running one to stop. The request_audit tool enqueues 'run' rows
-- (the column default), cancel_audit enqueues 'cancel' rows; the AuditDispatchWorker
-- branches on the action.

BEGIN;

ALTER TABLE audit_dispatch_log
    ADD COLUMN IF NOT EXISTS action text NOT NULL DEFAULT 'run';

INSERT INTO __migrations (version) VALUES ('0019_audit_dispatch_action')
ON CONFLICT (version) DO NOTHING;

COMMIT;
