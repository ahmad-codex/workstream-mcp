-- Carry a rendered Block Kit payload on the Slack outbox. When blocks_json is present
-- the SlackNotifyWorker posts it inside the coloured attachment (the `blocks` array),
-- giving each notification a rich layout — header, a project / plan / task field grid,
-- a persona context line, and deep-link buttons. The plain `body` column stays as the
-- attachment `fallback` for notifications and older clients. NULL blocks_json means
-- "post the plain body" (plan.* notifications, legacy rows).
--
-- The blocks are rendered at enqueue time from the same tokens as `body`, so the worker
-- stays free of plan-type lookups.

BEGIN;

ALTER TABLE slack_notify_log
    ADD COLUMN IF NOT EXISTS blocks_json text;

INSERT INTO __migrations (version) VALUES ('0017_slack_notify_blocks')
ON CONFLICT (version) DO NOTHING;

COMMIT;
