-- Carry the actor's github username on the board-sync outbox so the worker can
-- assign the corresponding Projects V2 card to that user. NULL means "do not
-- change assignees" (preserves existing behavior for legacy / non-human rows).

BEGIN;

ALTER TABLE board_sync_log
    ADD COLUMN IF NOT EXISTS assignee_github_username text;

INSERT INTO __migrations (version) VALUES ('0008_board_sync_assignee')
ON CONFLICT (version) DO NOTHING;

COMMIT;
