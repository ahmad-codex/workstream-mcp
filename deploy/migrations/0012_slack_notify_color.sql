-- Carry an attachment colour on the Slack outbox so the SlackNotifyWorker can post
-- each notification as a Slack attachment with a coloured left bar keyed to the
-- acting role (auditor / verifier / fixer / fix_verifier). NULL means "post as a
-- plain message with no colour bar" (plan.* notifications, legacy rows).
--
-- The colour is resolved at enqueue time from the plan-type's role_personas, so the
-- worker stays free of plan-type lookups.

BEGIN;

ALTER TABLE slack_notify_log
    ADD COLUMN IF NOT EXISTS color text;

INSERT INTO __migrations (version) VALUES ('0012_slack_notify_color')
ON CONFLICT (version) DO NOTHING;

COMMIT;
