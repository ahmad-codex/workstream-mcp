-- Fix the trigger function from 0001_initial.sql.
-- plpgsql resolves NEW.<col> at execution time, so referencing NEW.actor_id (only present
-- on attempts) inside a CASE that never reaches it for tasks/findings still trips the
-- "record new has no field actor_id" error when fired on those tables. Gate the access
-- on TG_TABLE_NAME with explicit IF branches instead.

BEGIN;

CREATE OR REPLACE FUNCTION workstream_emit_created_event() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_actor_id uuid;
    v_to_state text;
    v_entity_type text;
BEGIN
    IF TG_TABLE_NAME = 'attempts' THEN
        v_actor_id    := NEW.actor_id;
        v_to_state    := NULL;
        v_entity_type := 'attempt';
    ELSIF TG_TABLE_NAME = 'findings' THEN
        v_actor_id    := NULL;
        v_to_state    := NEW.status;
        v_entity_type := 'finding';
    ELSE  -- tasks
        v_actor_id    := NULL;
        v_to_state    := NEW.status;
        v_entity_type := 'task';
    END IF;

    INSERT INTO events (actor_id, entity_type, entity_id, event_type, to_state, payload)
    VALUES (v_actor_id, v_entity_type, NEW.id, 'created', v_to_state, '{}'::jsonb);
    RETURN NEW;
END;
$$;

INSERT INTO __migrations (version) VALUES ('0003_fix_created_event_trigger')
ON CONFLICT (version) DO NOTHING;

COMMIT;
