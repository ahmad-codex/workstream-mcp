-- A development task sent back by a reviewer (changes_requested below the retry cap) used to
-- return to in_progress with its claim cleared. claim_next_task and claim_specific_task only
-- pick pending tasks (or claimed tasks whose claim expired), so the task could never be
-- claimed again and every task depending on it stayed blocked (issue #70).
--
-- The transition now goes to pending, where the normal claim path picks it up. The attempt
-- count lives in the attempts table, so the retry cap still applies. The canonical source is
-- deploy/profiles/development.json; this migration keeps existing deployments aligned.

BEGIN;

UPDATE plan_types
SET state_graph = jsonb_set(
    state_graph,
    '{task_transitions}',
    (
        SELECT jsonb_agg(
            CASE
                WHEN t->>'from' = 'review' AND t->>'to' = 'in_progress'
                     AND t->>'via' = 'submit_review_decision' AND t->>'verdict' = 'changes_requested'
                THEN jsonb_set(t, '{to}', '"pending"')
                ELSE t
            END
            ORDER BY ord)
        FROM jsonb_array_elements(state_graph->'task_transitions') WITH ORDINALITY AS x(t, ord)
    ))
WHERE id = 'development';

-- Release tasks already stranded by the old transition: in_progress with no claim, in a
-- development plan. Normal in_progress work always holds a claim. The status-change trigger
-- records each release in the events table.
UPDATE tasks t
SET status = 'pending', updated_at = now()
FROM plans p
WHERE p.id = t.plan_id
  AND p.plan_type_id = 'development'
  AND t.status = 'in_progress'
  AND t.claim_token IS NULL;

INSERT INTO __migrations (version) VALUES ('0021_rework_returns_to_pending')
ON CONFLICT (version) DO NOTHING;

COMMIT;
