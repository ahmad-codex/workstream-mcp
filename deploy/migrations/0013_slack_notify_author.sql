-- Carry the acting actor's display identity on the Slack outbox so the SlackNotifyWorker
-- can render it as the attachment author row. For human actors author_icon is the public
-- GitHub avatar (https://github.com/<login>.png) and author_link is their profile, so a
-- human-handled task shows the person's real photo next to their name. All three are
-- NULL for AI actors, which keep their role-persona emoji line in the message body.

BEGIN;

ALTER TABLE slack_notify_log
    ADD COLUMN IF NOT EXISTS author_name text,
    ADD COLUMN IF NOT EXISTS author_icon text,
    ADD COLUMN IF NOT EXISTS author_link text;

INSERT INTO __migrations (version) VALUES ('0013_slack_notify_author')
ON CONFLICT (version) DO NOTHING;

COMMIT;
