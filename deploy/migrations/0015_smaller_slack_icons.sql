-- Shrink the Slack persona icons and drop the commit hash from fix.confirmed.
--   * role_personas emoji: the heavy :large_*_circle: glyphs become :black_small_square:
--     for both the audit and development profiles. Role colour is still carried by the
--     attachment colour bar, so the small neutral marker loses no information.
--   * audit fix.confirmed template no longer prints "— commit `{commit}`" — the commit
--     lives in the event log and the board card ledger; the channel stays scannable.
-- Keep deploy/profiles/audit.json and development.json in sync with these literals.

BEGIN;

UPDATE plan_types
SET config = jsonb_set(
        jsonb_set(
            config,
            '{role_personas}',
            $${
              "auditor":      { "persona": "Smith", "label": "Auditor",      "emoji": ":black_small_square:", "color": "#2563EB" },
              "verifier":     { "persona": "Jones", "label": "Verifier",     "emoji": ":black_small_square:", "color": "#7C3AED" },
              "fixer":        { "persona": "Brown", "label": "Fixer",        "emoji": ":black_small_square:", "color": "#EA580C" },
              "fix_verifier": { "persona": "Davis", "label": "Fix-Verifier", "emoji": ":black_small_square:", "color": "#16A34A" }
            }$$::jsonb,
            true
        ),
        '{slack_templates,fix.confirmed}',
        $$"{actor_line}\n:white_check_mark: Fix confirmed for {finding_link}"$$::jsonb,
        true
    ),
    updated_at = now()
WHERE id = 'audit';

UPDATE plan_types
SET config = jsonb_set(
        config,
        '{role_personas}',
        $${
          "developer": { "persona": "Developer", "label": "Developer", "emoji": ":black_small_square:", "color": "#2563EB" },
          "reviewer":  { "persona": "Reviewer",  "label": "Reviewer",  "emoji": ":black_small_square:", "color": "#7C3AED" }
        }$$::jsonb,
        true
    ),
    updated_at = now()
WHERE id = 'development';

INSERT INTO __migrations (version) VALUES ('0015_smaller_slack_icons')
ON CONFLICT (version) DO NOTHING;

COMMIT;
