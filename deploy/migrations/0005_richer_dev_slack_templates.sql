-- Replace the development plan-type's slack_templates with versions that:
--   * use the {task_title_link} token (clickable mrkdwn link to the bound board),
--   * include the actor on task.review (was missing),
--   * drop the literal commit-hash line on task.done (description carries the meaning).
--
-- Tokens added by the OutboxSlackNotifyEnqueue adapter: task_title_link, board_url.

BEGIN;

UPDATE plan_types
SET config = jsonb_set(
        config,
        '{slack_templates}',
        $${
          "task.created":     ":clipboard: New task: {task_title_link}",
          "task.claimed":     ":wave: *{actor}* claimed {task_title_link}",
          "task.in_progress": ":wrench: In progress: {task_title_link} — _{actor}_",
          "task.review":      ":eyes: In review: {task_title_link} — _{actor}_",
          "task.done":        ":white_check_mark: Done: {task_title_link} — reviewed by _{reviewer}_\n> {description}",
          "task.blocked":     ":construction: Blocked: {task_title_link} — {reason}",
          "plan.activated":   ":rocket: Plan *{plan_name}* active on *{project}*",
          "plan.completed":   ":tada: Plan *{plan_name}* complete"
        }$$::jsonb,
        true
    ),
    updated_at = now()
WHERE id = 'development';

INSERT INTO __migrations (version) VALUES ('0005_richer_dev_slack_templates')
ON CONFLICT (version) DO NOTHING;

COMMIT;
