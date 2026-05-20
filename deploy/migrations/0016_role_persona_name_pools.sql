-- Give each role a pool of persona names instead of one fixed name. The Slack
-- adapter picks a name deterministically from the work item's id (task id for
-- auditor/developer posts, finding id for verifier/fixer/fix-verifier posts), so
-- every post for the same item shows the same name while parallel items read as
-- distinct agents. label / emoji / color stay per-role.
-- Keep deploy/profiles/audit.json and development.json in sync with these literals.

BEGIN;

UPDATE plan_types
SET config = jsonb_set(
        config,
        '{role_personas}',
        $${
          "auditor":      { "label": "Auditor",      "emoji": ":black_small_square:", "color": "#2563EB", "personas": ["Smith","Carter","Reed","Pope","Hayes","Lane","Ford","Webb","Knox","Pratt","Shaw","Boyd"] },
          "verifier":     { "label": "Verifier",     "emoji": ":black_small_square:", "color": "#7C3AED", "personas": ["Jones","Hale","Vance","Quinn","Frost","Dean","Marsh","Lowe","Page","Cross","Tate","Nash"] },
          "fixer":        { "label": "Fixer",        "emoji": ":black_small_square:", "color": "#EA580C", "personas": ["Brown","Wells","Banks","Cole","Hunt","Rhodes","Stark","Flynn","Burke","Sloan","Gray","Doyle"] },
          "fix_verifier": { "label": "Fix-Verifier", "emoji": ":black_small_square:", "color": "#16A34A", "personas": ["Davis","Mercer","Wolfe","Booth","Greer","Bishop","Chase","York","Wade","Finch","Nolan","Pierce"] }
        }$$::jsonb,
        true
    ),
    updated_at = now()
WHERE id = 'audit';

UPDATE plan_types
SET config = jsonb_set(
        config,
        '{role_personas}',
        $${
          "developer": { "label": "Developer", "emoji": ":black_small_square:", "color": "#2563EB", "personas": ["Reyes","Adler","Cohen","Diaz","Engel","Frey","Gould","Hart","Iqbal","Joshi","Kerr","Lamb"] },
          "reviewer":  { "label": "Reviewer",  "emoji": ":black_small_square:", "color": "#7C3AED", "personas": ["Mann","Novak","Ortiz","Park","Roth","Sato","Tran","Voss","Ward","Yates","Zane","Bell"] }
        }$$::jsonb,
        true
    ),
    updated_at = now()
WHERE id = 'development';

INSERT INTO __migrations (version) VALUES ('0016_role_persona_name_pools')
ON CONFLICT (version) DO NOTHING;

COMMIT;
