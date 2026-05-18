-- Add the numeric databaseId of the GitHub Projects V2 item alongside the existing
-- node id. The board-pane URL on github.com expects the numeric id:
--   github.com/orgs/<owner>/projects/<n>/views/1?pane=issue&itemId=<NUMBER>
-- Storing both lets us build clickable Slack/email links to the exact card.

BEGIN;

ALTER TABLE tasks
    ADD COLUMN IF NOT EXISTS github_board_item_number bigint;

INSERT INTO __migrations (version) VALUES ('0006_task_board_item_number')
ON CONFLICT (version) DO NOTHING;

COMMIT;
