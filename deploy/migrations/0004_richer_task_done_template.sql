-- Enrich the development plan-type's task.done Slack template so it surfaces
-- the task description, commit hash, reviewer, and attempt count instead of
-- just the title + bare {commit} placeholder. The richer template uses tokens
-- now passed by SubmitReviewDecisionTool (commit, reviewer, attempts, comments)
-- and the always-available token `description` populated by the slack adapter.

BEGIN;

UPDATE plan_types
SET config = jsonb_set(
        config,
        '{slack_templates,task.done}',
        '":white_check_mark: Done: *{task_title}* — reviewed by _{reviewer}_ in {attempts} attempt(s)\n> {description}\ncommit `{commit}`"'::jsonb,
        true
    ),
    updated_at = now()
WHERE id = 'development';

INSERT INTO __migrations (version) VALUES ('0004_richer_task_done_template')
ON CONFLICT (version) DO NOTHING;

COMMIT;
