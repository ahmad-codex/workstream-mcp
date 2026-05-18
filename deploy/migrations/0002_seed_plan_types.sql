-- Seed the two v1 plan-type profiles: audit and development.
-- The state graph lives in JSONB so adding plan types later is an INSERT, not a recompile (§5).
-- The canonical readable source of these profiles is deploy/profiles/*.json — these literals must stay in sync.

BEGIN;

INSERT INTO plan_types (id, display_name, requires_findings, retry_cap, role_ttls, state_graph, board_column_mapping, config)
VALUES (
    'audit',
    'Audit',
    true,
    3,
    $${
      "auditor":      "30m",
      "verifier":     "15m",
      "fixer":        "45m",
      "fix_verifier": "15m"
    }$$::jsonb,
    $${
      "task_states": ["pending","claimed","in_progress","review","done","deferred","blocked","needs_human_review","skipped","out_of_scope"],
      "task_initial": "pending",
      "task_terminal": ["done","deferred","needs_human_review","skipped","out_of_scope"],
      "task_transitions": [
        {"from":"pending","to":"claimed","via":"claim_task","requires_role":["auditor"]},
        {"from":"claimed","to":"in_progress","via":"start_work"},
        {"from":"claimed","to":"pending","via":"release_claim"},
        {"from":"in_progress","to":"review","via":"submit_findings","requires_role":["auditor"],"guard":"findings_non_empty"},
        {"from":"in_progress","to":"done","via":"submit_findings","requires_role":["auditor"],"guard":"findings_empty"},
        {"from":"in_progress","to":"pending","via":"release_claim"},
        {"from":"review","to":"done","via":"rollup","guard":"all_findings_terminal_resolved"},
        {"from":"review","to":"in_progress","via":"request_changes"},
        {"from":"*","to":"blocked","via":"mark_blocked","requires_permission":"can_mark_needs_human_review"},
        {"from":"blocked","to":"in_progress","via":"unblock"},
        {"from":"*","to":"deferred","via":"defer","requires_permission":"can_override_verdict"},
        {"from":"*","to":"skipped","via":"mark_skipped","requires_permission":"can_override_verdict"},
        {"from":"*","to":"out_of_scope","via":"mark_out_of_scope","requires_permission":"can_override_verdict"},
        {"from":"*","to":"needs_human_review","via":"escalate"}
      ],
      "finding_states": ["pending_verification","confirmed","rejected","ambiguous","in_fix","fixed","fix_failed","partial","needs_human_review","deferred"],
      "finding_initial": "pending_verification",
      "finding_terminal": ["fixed","rejected","needs_human_review","deferred"],
      "finding_transitions": [
        {"from":"pending_verification","to":"confirmed","via":"submit_verification_verdict","verdict":"confirmed","requires_role":["verifier"]},
        {"from":"pending_verification","to":"rejected","via":"submit_verification_verdict","verdict":"rejected","requires_role":["verifier"]},
        {"from":"pending_verification","to":"ambiguous","via":"submit_verification_verdict","verdict":"ambiguous","requires_role":["verifier"]},
        {"from":"ambiguous","to":"confirmed","via":"submit_verification_verdict","verdict":"confirmed","requires_role":["verifier"]},
        {"from":"ambiguous","to":"rejected","via":"submit_verification_verdict","verdict":"rejected","requires_role":["verifier"]},
        {"from":"confirmed","to":"in_fix","via":"claim_fix","requires_role":["fixer"]},
        {"from":"in_fix","to":"fixed","via":"submit_attempt_verdict","verdict":"fix_confirmed","requires_role":["fix_verifier"]},
        {"from":"in_fix","to":"fix_failed","via":"submit_attempt_verdict","verdict":"fix_failed","requires_role":["fix_verifier"]},
        {"from":"in_fix","to":"partial","via":"submit_attempt_verdict","verdict":"partial","requires_role":["fix_verifier"]},
        {"from":"fix_failed","to":"in_fix","via":"claim_fix","requires_role":["fixer"],"guard":"below_retry_cap"},
        {"from":"fix_failed","to":"needs_human_review","via":"escalate","guard":"at_retry_cap"},
        {"from":"partial","to":"in_fix","via":"claim_fix","requires_role":["fixer"],"guard":"below_retry_cap"},
        {"from":"*","to":"needs_human_review","via":"escalate"},
        {"from":"*","to":"deferred","via":"defer","requires_permission":"can_override_verdict"}
      ]
    }$$::jsonb,
    $${
      "pending":            "backlog",
      "claimed":            "backlog",
      "in_progress":        "in_progress",
      "review":             "review",
      "done":               "done",
      "blocked":            "blocked",
      "deferred":           "done",
      "needs_human_review": "blocked",
      "skipped":            "done",
      "out_of_scope":       "done"
    }$$::jsonb,
    $${
      "roles": {
        "auditor":      {"primary_execution": true,  "is_reviewer": false},
        "verifier":     {"primary_execution": false, "is_reviewer": true},
        "fixer":        {"primary_execution": false, "is_reviewer": false},
        "fix_verifier": {"primary_execution": false, "is_reviewer": true}
      },
      "slack_templates": {
        "task.claimed":      ":wave: *{actor}* claimed audit task *{task_title}*",
        "task.in_progress":  ":wrench: Auditing in progress: *{task_title}* — _{actor}_",
        "task.review":       ":eyes: Audit findings submitted for *{task_title}*",
        "task.done":         ":white_check_mark: Audit task done: *{task_title}*",
        "task.blocked":      ":construction: Blocked: *{task_title}* — {reason}",
        "finding.confirmed": ":red_circle: Finding *{finding_key}* confirmed ({severity})",
        "finding.rejected":  ":white_circle: Finding *{finding_key}* rejected — {reason}",
        "fix.confirmed":     ":white_check_mark: Fix confirmed for *{finding_key}*",
        "fix.failed":        ":x: Fix failed for *{finding_key}* — {reason}",
        "plan.activated":    ":rocket: Audit plan *{plan_name}* active on *{project}*",
        "plan.completed":    ":tada: Audit plan *{plan_name}* complete"
      },
      "default_notify_on": ["task.claimed","task.in_progress","task.review","task.done","task.blocked","finding.confirmed","fix.confirmed","fix.failed","plan.activated","plan.completed"]
    }$$::jsonb
)
ON CONFLICT (id) DO UPDATE SET
    display_name        = EXCLUDED.display_name,
    requires_findings   = EXCLUDED.requires_findings,
    retry_cap           = EXCLUDED.retry_cap,
    role_ttls           = EXCLUDED.role_ttls,
    state_graph         = EXCLUDED.state_graph,
    board_column_mapping = EXCLUDED.board_column_mapping,
    config              = EXCLUDED.config,
    updated_at          = now();


INSERT INTO plan_types (id, display_name, requires_findings, retry_cap, role_ttls, state_graph, board_column_mapping, config)
VALUES (
    'development',
    'Development',
    false,
    3,
    $${
      "developer": "4h",
      "reviewer":  "1h"
    }$$::jsonb,
    $${
      "task_states": ["pending","claimed","in_progress","review","done","deferred","blocked","needs_human_review","skipped","out_of_scope"],
      "task_initial": "pending",
      "task_terminal": ["done","deferred","needs_human_review","skipped","out_of_scope"],
      "task_transitions": [
        {"from":"pending","to":"claimed","via":"claim_task","requires_role":["developer"]},
        {"from":"claimed","to":"in_progress","via":"start_work"},
        {"from":"claimed","to":"pending","via":"release_claim"},
        {"from":"in_progress","to":"review","via":"submit_attempt","requires_role":["developer"]},
        {"from":"in_progress","to":"pending","via":"release_claim"},
        {"from":"review","to":"done","via":"submit_review_decision","verdict":"approved","requires_role":["reviewer"]},
        {"from":"review","to":"in_progress","via":"submit_review_decision","verdict":"changes_requested","requires_role":["reviewer"],"guard":"below_retry_cap"},
        {"from":"review","to":"needs_human_review","via":"submit_review_decision","verdict":"changes_requested","requires_role":["reviewer"],"guard":"at_retry_cap"},
        {"from":"*","to":"blocked","via":"mark_blocked","requires_permission":"can_mark_needs_human_review"},
        {"from":"blocked","to":"in_progress","via":"unblock"},
        {"from":"*","to":"deferred","via":"defer","requires_permission":"can_override_verdict"},
        {"from":"*","to":"skipped","via":"mark_skipped","requires_permission":"can_override_verdict"},
        {"from":"*","to":"out_of_scope","via":"mark_out_of_scope","requires_permission":"can_override_verdict"},
        {"from":"*","to":"needs_human_review","via":"escalate"}
      ],
      "finding_states": [],
      "finding_initial": null,
      "finding_terminal": [],
      "finding_transitions": []
    }$$::jsonb,
    $${
      "pending":            "backlog",
      "claimed":            "backlog",
      "in_progress":        "in_progress",
      "review":             "review",
      "done":               "done",
      "blocked":            "blocked",
      "deferred":           "done",
      "needs_human_review": "blocked",
      "skipped":            "done",
      "out_of_scope":       "done"
    }$$::jsonb,
    $${
      "roles": {
        "developer": {"primary_execution": true,  "is_reviewer": false},
        "reviewer":  {"primary_execution": false, "is_reviewer": true}
      },
      "slack_templates": {
        "task.created":     ":clipboard: New task: *{task_title}*",
        "task.claimed":     ":wave: *{actor}* claimed *{task_title}*",
        "task.in_progress": ":wrench: In progress: *{task_title}* — _{actor}_",
        "task.review":      ":eyes: In review: *{task_title}*",
        "task.done":        ":white_check_mark: Done: *{task_title}* (commit `{commit}`)",
        "task.blocked":     ":construction: Blocked: *{task_title}* — {reason}",
        "plan.activated":   ":rocket: Plan *{plan_name}* active on *{project}*",
        "plan.completed":   ":tada: Plan *{plan_name}* complete"
      },
      "default_notify_on": ["task.created","task.claimed","task.in_progress","task.review","task.done","task.blocked","plan.activated","plan.completed"]
    }$$::jsonb
)
ON CONFLICT (id) DO UPDATE SET
    display_name        = EXCLUDED.display_name,
    requires_findings   = EXCLUDED.requires_findings,
    retry_cap           = EXCLUDED.retry_cap,
    role_ttls           = EXCLUDED.role_ttls,
    state_graph         = EXCLUDED.state_graph,
    board_column_mapping = EXCLUDED.board_column_mapping,
    config              = EXCLUDED.config,
    updated_at          = now();


INSERT INTO __migrations (version) VALUES ('0002_seed_plan_types')
ON CONFLICT (version) DO NOTHING;

COMMIT;
