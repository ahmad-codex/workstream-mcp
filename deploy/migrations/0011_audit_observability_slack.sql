-- Audit-observability Slack overhaul.
--
-- Rewrites the audit and development plan-type `config` JSONB so Slack posts:
--   * carry a computed {actor_line} header — role persona, role label, and an
--     AI Agent / Human tag (audit work is agent-driven; dev work can be either),
--   * are clickable (audit task templates now use {task_title_link}; findings use
--     {finding_link} which deep-links to the parent task's board card),
--   * cover every audit lifecycle state with its own template — confirmed,
--     rejected, ambiguous, escalated-to-human, deferred, partial fix — instead of
--     mislabelling an escalation as a "rejection",
--   * carry a {reason} that is actually substituted (the literal "{reason}" bug).
--
-- New config keys consumed by OutboxSlackNotifyEnqueue:
--   role_personas  — role -> { persona, label, emoji, color }
--   slack_roles    — notification_type -> role (drives {actor_line} + the color bar)
--
-- The canonical readable source is deploy/profiles/{audit,development}.json — these
-- literals must stay in sync with those files.

BEGIN;

UPDATE plan_types
SET config = $${
  "roles": {
    "auditor":      {"primary_execution": true,  "is_reviewer": false},
    "verifier":     {"primary_execution": false, "is_reviewer": true},
    "fixer":        {"primary_execution": false, "is_reviewer": false},
    "fix_verifier": {"primary_execution": false, "is_reviewer": true}
  },
  "role_personas": {
    "auditor":      {"persona": "Smith", "label": "Auditor",      "emoji": ":large_blue_circle:",   "color": "#2563EB"},
    "verifier":     {"persona": "Jones", "label": "Verifier",     "emoji": ":large_purple_circle:", "color": "#7C3AED"},
    "fixer":        {"persona": "Brown", "label": "Fixer",        "emoji": ":large_orange_circle:", "color": "#EA580C"},
    "fix_verifier": {"persona": "Davis", "label": "Fix-Verifier", "emoji": ":large_green_circle:",  "color": "#16A34A"}
  },
  "slack_roles": {
    "task.claimed":      "auditor",
    "task.in_progress":  "auditor",
    "task.review":       "auditor",
    "task.done":         "auditor",
    "finding.confirmed": "verifier",
    "finding.rejected":  "verifier",
    "finding.ambiguous": "verifier",
    "fix.confirmed":     "fix_verifier",
    "fix.failed":        "fix_verifier",
    "fix.partial":       "fix_verifier"
  },
  "slack_templates": {
    "task.claimed":               "{actor_line}\n:inbox_tray: Claimed audit task {task_title_link}",
    "task.in_progress":           "{actor_line}\n:mag: Auditing {task_title_link}",
    "task.review":                "{actor_line}\n:clipboard: Submitted {finding_count} finding(s) for review on {task_title_link}",
    "task.done":                  "{actor_line}\n:white_check_mark: Audit task complete — {task_title_link}",
    "task.blocked":               "{actor_line}\n:no_entry: Blocked — {task_title_link}\n> {reason}",
    "task.needs_human_review":    "{actor_line}\n:rotating_light: Needs human review — {task_title_link}\n> {reason}",
    "finding.confirmed":          "{actor_line}\n{severity_emoji} Confirmed *{severity}* finding {finding_link}\n> on audit task {task_title_link}",
    "finding.rejected":           "{actor_line}\n:white_circle: Rejected finding {finding_link}\n> on audit task {task_title_link} — reason: {reason}",
    "finding.ambiguous":          "{actor_line}\n:grey_question: Ambiguous finding {finding_link}\n> on audit task {task_title_link} — reason: {reason}",
    "finding.needs_human_review": "{actor_line}\n:rotating_light: Escalated to human review — {finding_link}\n> on audit task {task_title_link}\n> reason: {reason}",
    "finding.deferred":           "{actor_line}\n:double_vertical_bar: Finding deferred — {finding_link}\n> reason: {reason}",
    "fix.confirmed":              "{actor_line}\n:white_check_mark: Fix confirmed for {finding_link} — commit `{commit}`",
    "fix.failed":                 "{actor_line}\n:x: Fix failed for {finding_link} — attempt {attempt}\n> reason: {reason}",
    "fix.partial":                "{actor_line}\n:large_yellow_circle: Partial fix for {finding_link} — residual tracked as a child finding",
    "plan.activated":             ":rocket: Audit plan *{plan_name}* is now active",
    "plan.completed":             ":tada: Audit plan *{plan_name}* complete — every task reached a terminal state"
  },
  "default_notify_on": [
    "task.claimed","task.in_progress","task.review","task.done","task.blocked","task.needs_human_review",
    "finding.confirmed","finding.rejected","finding.ambiguous","finding.needs_human_review","finding.deferred",
    "fix.confirmed","fix.failed","fix.partial",
    "plan.activated","plan.completed"
  ]
}$$::jsonb,
    updated_at = now()
WHERE id = 'audit';

UPDATE plan_types
SET config = $${
  "roles": {
    "developer": {"primary_execution": true,  "is_reviewer": false},
    "reviewer":  {"primary_execution": false, "is_reviewer": true}
  },
  "role_personas": {
    "developer": {"persona": "Developer", "label": "Developer", "emoji": ":hammer_and_wrench:", "color": "#2563EB"},
    "reviewer":  {"persona": "Reviewer",  "label": "Reviewer",  "emoji": ":eyes:",              "color": "#7C3AED"}
  },
  "slack_roles": {
    "task.created":     "developer",
    "task.claimed":     "developer",
    "task.in_progress": "developer",
    "task.review":      "developer",
    "task.done":        "reviewer"
  },
  "slack_templates": {
    "task.created":     "{actor_line}\n:clipboard: New task: {task_title_link}",
    "task.claimed":     "{actor_line}\n:inbox_tray: Claimed {task_title_link}",
    "task.in_progress": "{actor_line}\n:wrench: In progress: {task_title_link}",
    "task.review":      "{actor_line}\n:eyes: In review: {task_title_link}",
    "task.done":        "{actor_line}\n:white_check_mark: Done: {task_title_link}\n> {description}",
    "task.blocked":     "{actor_line}\n:no_entry: Blocked: {task_title_link}\n> {reason}",
    "task.needs_human_review": "{actor_line}\n:rotating_light: Needs human review: {task_title_link}\n> {reason}",
    "plan.activated":   ":rocket: Plan *{plan_name}* is now active",
    "plan.completed":   ":tada: Plan *{plan_name}* complete"
  },
  "default_notify_on": [
    "task.created","task.claimed","task.in_progress","task.review","task.done","task.blocked","task.needs_human_review",
    "plan.activated","plan.completed"
  ]
}$$::jsonb,
    updated_at = now()
WHERE id = 'development';

INSERT INTO __migrations (version) VALUES ('0011_audit_observability_slack')
ON CONFLICT (version) DO NOTHING;

COMMIT;
