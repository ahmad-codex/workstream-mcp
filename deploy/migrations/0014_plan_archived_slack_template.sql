-- Add the plan.archived Slack notification to the audit and development plan types.
--
-- archive_plan (the new disable-a-plan tool) posts a plan.archived notice the same way
-- activate_plan posts plan.activated. This migration adds the template string and the
-- default_notify_on trigger to plan_types.config, surgically on top of 0011 — only the
-- two new keys are touched. Mirrors deploy/profiles/{audit,development}.json, which carry
-- the same additions as the canonical readable source.
--
-- The WHERE guard makes the UPDATE idempotent: re-running cannot append plan.archived to
-- default_notify_on twice.

BEGIN;

-- audit
UPDATE plan_types
SET config = jsonb_set(
                 config,
                 '{slack_templates,plan.archived}',
                 '":file_cabinet: Audit plan *{plan_name}* archived — disabled, no longer accepting work"'::jsonb)
             || jsonb_build_object(
                 'default_notify_on',
                 (config -> 'default_notify_on') || '["plan.archived"]'::jsonb),
    updated_at = now()
WHERE id = 'audit'
  AND NOT (config -> 'default_notify_on' ? 'plan.archived');

-- development
UPDATE plan_types
SET config = jsonb_set(
                 config,
                 '{slack_templates,plan.archived}',
                 '":file_cabinet: Plan *{plan_name}* archived — disabled, no longer accepting work"'::jsonb)
             || jsonb_build_object(
                 'default_notify_on',
                 (config -> 'default_notify_on') || '["plan.archived"]'::jsonb),
    updated_at = now()
WHERE id = 'development'
  AND NOT (config -> 'default_notify_on' ? 'plan.archived');

INSERT INTO __migrations (version) VALUES ('0014_plan_archived_slack_template')
ON CONFLICT (version) DO NOTHING;

COMMIT;
