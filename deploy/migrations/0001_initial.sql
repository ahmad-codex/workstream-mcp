-- workstream-mcp initial schema
-- Authoritative DDL for §4. Every column listed here is referenced by either the
-- claim primitive (§6), the state machine (§5), or the outbox patterns (§7.5, §8.3).
--
-- Conventions:
--   * uuid PKs default to gen_random_uuid()  (Postgres 13+ ships this in core)
--   * timestamps are timestamptz, default now()
--   * citext is used for case-insensitive GitHub usernames
--   * append-only tables (events, verdicts) are guarded by triggers
--   * claim columns are all-or-nothing per row, enforced by check constraint
--
-- Run on a clean database. The C# migration runner records this file as applied
-- in the __migrations table created at the bottom.

BEGIN;

CREATE EXTENSION IF NOT EXISTS citext;
CREATE EXTENSION IF NOT EXISTS pgcrypto;  -- for gen_random_uuid on older builds

--==============================================================================
-- Identity and tenancy
--==============================================================================

CREATE TABLE organizations (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name        text NOT NULL UNIQUE,
    created_at  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE users (
    id                              uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    github_username                 citext NOT NULL UNIQUE,
    display_name                    text,
    mcp_url_token                   text NOT NULL UNIQUE,
    actor_type                      text NOT NULL,
    is_active                       boolean NOT NULL DEFAULT true,
    can_override_verdict            boolean NOT NULL DEFAULT false,
    can_archive_plan                boolean NOT NULL DEFAULT false,
    can_mark_needs_human_review     boolean NOT NULL DEFAULT true,
    is_admin                        boolean NOT NULL DEFAULT false,
    config                          jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at                      timestamptz NOT NULL DEFAULT now(),
    last_seen_at                    timestamptz,

    CONSTRAINT users_actor_type_ck
        CHECK (actor_type IN ('human','orchestrator','subagent'))
);

CREATE INDEX idx_users_active ON users (mcp_url_token) WHERE is_active = true;

CREATE TABLE revoked_tokens (
    token_hash  text PRIMARY KEY,         -- SHA-256 of revoked token, hex-encoded
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    revoked_at  timestamptz NOT NULL DEFAULT now(),
    expires_at  timestamptz NOT NULL
);

CREATE INDEX idx_revoked_tokens_expiry ON revoked_tokens (expires_at);

CREATE TABLE token_usage (
    id          bigserial PRIMARY KEY,
    user_id     uuid NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    at          timestamptz NOT NULL DEFAULT now(),
    tool_name   text,
    success     boolean NOT NULL,
    ip          inet
);

CREATE INDEX idx_token_usage_user_time ON token_usage (user_id, at DESC);

--==============================================================================
-- Projects, repos, boards, slack
--==============================================================================

CREATE TABLE projects (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    organization_id     uuid REFERENCES organizations(id) ON DELETE SET NULL,
    slug                text NOT NULL UNIQUE,
    display_name        text NOT NULL,
    description         text,
    config              jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE project_repos (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id          uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    github_owner        text NOT NULL,
    github_repo         text NOT NULL,
    default_branch      text NOT NULL DEFAULT 'main',
    is_reference_only   boolean NOT NULL DEFAULT false,
    created_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT project_repos_unique UNIQUE (project_id, github_owner, github_repo)
);

CREATE TABLE project_boards (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id                  uuid NOT NULL REFERENCES projects(id) ON DELETE CASCADE,
    github_project_v2_node_id   text NOT NULL UNIQUE,
    github_project_number       integer NOT NULL,
    github_owner                text NOT NULL,
    display_name                text NOT NULL,
    status_field_node_id        text NOT NULL,
    status_option_backlog       text NOT NULL,
    status_option_in_progress   text NOT NULL,
    status_option_review        text NOT NULL,
    status_option_done          text NOT NULL,
    status_option_blocked       text,
    config                      jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at                  timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE project_slack (
    project_id              uuid PRIMARY KEY REFERENCES projects(id) ON DELETE CASCADE,
    workspace_id            text NOT NULL,
    bot_token_secret_ref    text NOT NULL,
    default_channel_id      text NOT NULL,
    notify_on               text[] NOT NULL DEFAULT ARRAY[
                                'task.claimed',
                                'task.in_progress',
                                'task.review',
                                'task.done',
                                'task.blocked',
                                'finding.confirmed',
                                'fix.failed'
                            ]::text[],
    created_at              timestamptz NOT NULL DEFAULT now()
);

--==============================================================================
-- Plan types and plans
--==============================================================================

CREATE TABLE plan_types (
    id                  text PRIMARY KEY,
    display_name        text NOT NULL,
    state_graph         jsonb NOT NULL,
    role_ttls           jsonb NOT NULL,
    retry_cap           integer NOT NULL DEFAULT 3,
    prompt_template_ref text,
    requires_findings   boolean NOT NULL DEFAULT false,
    board_column_mapping jsonb NOT NULL DEFAULT '{}'::jsonb,
    config              jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at          timestamptz NOT NULL DEFAULT now(),
    updated_at          timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE plans (
    id                          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id                  uuid NOT NULL REFERENCES projects(id) ON DELETE RESTRICT,
    plan_type_id                text NOT NULL REFERENCES plan_types(id) ON DELETE RESTRICT,
    name                        text NOT NULL,
    objective                   text,
    status                      text NOT NULL DEFAULT 'draft',
    created_by_actor_id         uuid REFERENCES users(id) ON DELETE SET NULL,
    primary_board_id            uuid REFERENCES project_boards(id) ON DELETE SET NULL,
    primary_slack_channel_id    text,
    config                      jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at                  timestamptz NOT NULL DEFAULT now(),
    activated_at                timestamptz,
    completed_at                timestamptz,

    CONSTRAINT plans_status_ck
        CHECK (status IN ('draft','active','paused','completed','archived'))
);

CREATE INDEX idx_plans_project_status ON plans (project_id, status);

--==============================================================================
-- Phases, tasks, findings, attempts, verdicts
--==============================================================================

CREATE TABLE phases (
    id          uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_id     uuid NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    order_index integer NOT NULL,
    name        text NOT NULL,
    status      text NOT NULL DEFAULT 'pending',
    created_at  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT phases_unique UNIQUE (plan_id, order_index)
);

-- Status values are the UNION of every plan-type's state list. The state machine
-- enforces per-plan-type legality at the application layer (§5.3); this check
-- only guards against arbitrary garbage.
CREATE TABLE tasks (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    plan_id                 uuid NOT NULL REFERENCES plans(id) ON DELETE CASCADE,
    phase_id                uuid REFERENCES phases(id) ON DELETE SET NULL,
    external_key            text NOT NULL,
    title                   text NOT NULL,
    description             text,
    paths                   text[],
    reference_pointer       text,
    priority                integer NOT NULL DEFAULT 0,
    status                  text NOT NULL DEFAULT 'pending',
    assignee_actor_id       uuid REFERENCES users(id) ON DELETE SET NULL,
    github_board_item_id    text,
    github_issue_number     integer,
    github_issue_node_id    text,

    -- Claim columns (§6). All-or-nothing per the check constraint below.
    claim_actor_id          uuid REFERENCES users(id) ON DELETE SET NULL,
    claim_role              text,
    claim_token             uuid,
    claimed_at              timestamptz,
    claimed_until           timestamptz,

    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT tasks_external_key_unique UNIQUE (plan_id, external_key),

    CONSTRAINT tasks_status_ck CHECK (status IN (
        'pending','claimed','in_progress','review','done',
        'deferred','blocked','needs_human_review',
        'skipped','out_of_scope'
    )),

    CONSTRAINT tasks_claim_consistency_ck CHECK (
        (claim_actor_id IS NULL AND claim_token IS NULL AND claimed_at IS NULL AND claimed_until IS NULL AND claim_role IS NULL)
        OR
        (claim_actor_id IS NOT NULL AND claim_token IS NOT NULL AND claimed_at IS NOT NULL AND claimed_until IS NOT NULL AND claim_role IS NOT NULL)
    )
);

-- Hot path: claim_next_task scan
CREATE INDEX idx_tasks_plan_status_priority
    ON tasks (plan_id, status, priority DESC, created_at ASC);

-- For TTL-recovery sweepers and stuck-work queries
CREATE INDEX idx_tasks_claimed_until
    ON tasks (claimed_until) WHERE claim_token IS NOT NULL;

CREATE INDEX idx_tasks_assignee ON tasks (assignee_actor_id) WHERE assignee_actor_id IS NOT NULL;


CREATE TABLE findings (
    id                      uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    task_id                 uuid NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    external_key            text NOT NULL,
    severity                text,
    invariant_impact        text,
    symptom                 text,
    root_cause              text,
    repro_steps             text,
    adversarial_input       text,
    expected                text,
    actual                  text,
    reference_comparison    text,
    status                  text NOT NULL DEFAULT 'pending_verification',

    claim_actor_id          uuid REFERENCES users(id) ON DELETE SET NULL,
    claim_role              text,
    claim_token             uuid,
    claimed_at              timestamptz,
    claimed_until           timestamptz,

    created_at              timestamptz NOT NULL DEFAULT now(),
    updated_at              timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT findings_external_key_unique UNIQUE (task_id, external_key),

    CONSTRAINT findings_status_ck CHECK (status IN (
        'pending_verification','confirmed','rejected','ambiguous',
        'in_fix','fixed','fix_failed','partial','needs_human_review','deferred'
    )),

    CONSTRAINT findings_claim_consistency_ck CHECK (
        (claim_actor_id IS NULL AND claim_token IS NULL AND claimed_at IS NULL AND claimed_until IS NULL AND claim_role IS NULL)
        OR
        (claim_actor_id IS NOT NULL AND claim_token IS NOT NULL AND claimed_at IS NOT NULL AND claimed_until IS NOT NULL AND claim_role IS NOT NULL)
    )
);

CREATE INDEX idx_findings_task_status ON findings (task_id, status);
CREATE INDEX idx_findings_status_severity ON findings (status, severity);
CREATE INDEX idx_findings_claimed_until ON findings (claimed_until) WHERE claim_token IS NOT NULL;


CREATE TABLE attempts (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    finding_id          uuid REFERENCES findings(id) ON DELETE CASCADE,
    task_id             uuid REFERENCES tasks(id) ON DELETE CASCADE,
    attempt_number      integer NOT NULL,
    files_changed       text[],
    approach_summary    text,
    side_effects        text,
    build_command       text,
    test_scenario       text,
    diff_ref            text,
    commit_hash         text,
    actor_id            uuid REFERENCES users(id) ON DELETE SET NULL,

    claim_actor_id      uuid REFERENCES users(id) ON DELETE SET NULL,
    claim_role          text,
    claim_token         uuid,
    claimed_at          timestamptz,
    claimed_until       timestamptz,

    created_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT attempts_one_parent_ck CHECK (
        (finding_id IS NOT NULL AND task_id IS NULL)
        OR (finding_id IS NULL AND task_id IS NOT NULL)
    ),

    CONSTRAINT attempts_claim_consistency_ck CHECK (
        (claim_actor_id IS NULL AND claim_token IS NULL AND claimed_at IS NULL AND claimed_until IS NULL AND claim_role IS NULL)
        OR
        (claim_actor_id IS NOT NULL AND claim_token IS NOT NULL AND claimed_at IS NOT NULL AND claimed_until IS NOT NULL AND claim_role IS NOT NULL)
    )
);

CREATE INDEX idx_attempts_finding ON attempts (finding_id, attempt_number) WHERE finding_id IS NOT NULL;
CREATE INDEX idx_attempts_task    ON attempts (task_id,    attempt_number) WHERE task_id    IS NOT NULL;
CREATE INDEX idx_attempts_claimed_until ON attempts (claimed_until) WHERE claim_token IS NOT NULL;


CREATE TABLE verdicts (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    finding_id          uuid REFERENCES findings(id) ON DELETE CASCADE,
    attempt_id          uuid REFERENCES attempts(id) ON DELETE CASCADE,
    verdict_type        text NOT NULL,
    reason_category     text,
    pre_output          text,
    post_output         text,
    adversarial_output  text,
    invariant_evidence  jsonb,
    actor_id            uuid REFERENCES users(id) ON DELETE SET NULL,
    at                  timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT verdicts_one_parent_ck CHECK (
        (finding_id IS NOT NULL AND attempt_id IS NULL)
        OR (finding_id IS NULL AND attempt_id IS NOT NULL)
    ),
    CONSTRAINT verdicts_type_ck CHECK (verdict_type IN (
        'confirmed','rejected','ambiguous',
        'fix_confirmed','fix_failed','partial',
        'approved','changes_requested'
    ))
);

CREATE INDEX idx_verdicts_finding ON verdicts (finding_id, at DESC) WHERE finding_id IS NOT NULL;
CREATE INDEX idx_verdicts_attempt ON verdicts (attempt_id, at DESC) WHERE attempt_id IS NOT NULL;
CREATE INDEX idx_verdicts_actor   ON verdicts (actor_id, at DESC);

--==============================================================================
-- Events (universal append-only log)
--==============================================================================

CREATE TABLE events (
    id          bigserial PRIMARY KEY,
    at          timestamptz NOT NULL DEFAULT now(),
    actor_id    uuid REFERENCES users(id) ON DELETE SET NULL,
    entity_type text NOT NULL,
    entity_id   uuid NOT NULL,
    event_type  text NOT NULL,
    from_state  text,
    to_state    text,
    payload     jsonb NOT NULL DEFAULT '{}'::jsonb
);

CREATE INDEX idx_events_entity ON events (entity_type, entity_id, at DESC);
CREATE INDEX idx_events_at     ON events (at DESC);
CREATE INDEX idx_events_actor  ON events (actor_id, at DESC);
CREATE INDEX idx_events_type   ON events (event_type, at DESC);

--==============================================================================
-- Outbox tables
--==============================================================================

CREATE TABLE board_sync_log (
    id                  bigserial PRIMARY KEY,
    task_id             uuid REFERENCES tasks(id) ON DELETE CASCADE,
    board_id            uuid REFERENCES project_boards(id) ON DELETE CASCADE,
    target_column       text NOT NULL,
    target_status       text NOT NULL,                    -- internal status that triggered the sync
    sync_marker         uuid NOT NULL DEFAULT gen_random_uuid(),  -- echo-suppression marker (§7.6)
    attempts            integer NOT NULL DEFAULT 0,
    next_attempt_at     timestamptz NOT NULL DEFAULT now(),
    last_attempted_at   timestamptz,
    result              text NOT NULL DEFAULT 'pending',
    error               text,
    github_response_id  text,
    created_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT board_sync_result_ck
        CHECK (result IN ('pending','success','retry','failed'))
);

CREATE INDEX idx_board_sync_pending
    ON board_sync_log (next_attempt_at) WHERE result IN ('pending','retry');
CREATE INDEX idx_board_sync_task ON board_sync_log (task_id, created_at DESC);
CREATE INDEX idx_board_sync_marker ON board_sync_log (sync_marker);


CREATE TABLE slack_notify_log (
    id                  bigserial PRIMARY KEY,
    event_id            bigint REFERENCES events(id) ON DELETE SET NULL,
    project_id          uuid REFERENCES projects(id) ON DELETE SET NULL,
    plan_id             uuid REFERENCES plans(id) ON DELETE SET NULL,
    entity_type         text NOT NULL,
    entity_id           uuid NOT NULL,
    channel_id          text NOT NULL,
    notification_type   text NOT NULL,
    body                text NOT NULL,
    thread_ts           text,           -- parent ts if this is a reply
    slack_ts            text,           -- assigned on success
    attempts            integer NOT NULL DEFAULT 0,
    next_attempt_at     timestamptz NOT NULL DEFAULT now(),
    last_attempted_at   timestamptz,
    result              text NOT NULL DEFAULT 'pending',
    error               text,
    created_at          timestamptz NOT NULL DEFAULT now(),

    CONSTRAINT slack_notify_result_ck
        CHECK (result IN ('pending','success','retry','failed'))
);

CREATE INDEX idx_slack_notify_pending
    ON slack_notify_log (next_attempt_at) WHERE result IN ('pending','retry');
CREATE INDEX idx_slack_notify_entity
    ON slack_notify_log (entity_type, entity_id, created_at);

--==============================================================================
-- Webhooks and inbound items
--==============================================================================

CREATE TABLE webhook_deliveries (
    delivery_id text PRIMARY KEY,        -- X-GitHub-Delivery
    received_at timestamptz NOT NULL DEFAULT now(),
    event_name  text NOT NULL,
    payload     jsonb NOT NULL
);

CREATE INDEX idx_webhook_deliveries_received ON webhook_deliveries (received_at DESC);

CREATE TABLE unsorted_inbound_items (
    id                      bigserial PRIMARY KEY,
    board_id                uuid NOT NULL REFERENCES project_boards(id) ON DELETE CASCADE,
    github_board_item_id    text NOT NULL,
    title                   text,
    body                    text,
    raw_payload             jsonb NOT NULL,
    received_at             timestamptz NOT NULL DEFAULT now(),
    resolved_task_id        uuid REFERENCES tasks(id) ON DELETE SET NULL,
    resolved_at             timestamptz
);

CREATE INDEX idx_inbound_unresolved
    ON unsorted_inbound_items (board_id) WHERE resolved_task_id IS NULL;

CREATE TABLE stuck_work_reports (
    id              bigserial PRIMARY KEY,
    generated_at    timestamptz NOT NULL DEFAULT now(),
    plan_id         uuid REFERENCES plans(id) ON DELETE CASCADE,
    summary         jsonb NOT NULL
);

CREATE INDEX idx_stuck_work_recent ON stuck_work_reports (generated_at DESC);

--==============================================================================
-- Append-only guards
--==============================================================================

CREATE OR REPLACE FUNCTION workstream_block_mutation() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'table % is append-only', TG_TABLE_NAME
        USING ERRCODE = 'restrict_violation';
END;
$$;

CREATE TRIGGER events_block_update    BEFORE UPDATE ON events    FOR EACH ROW EXECUTE FUNCTION workstream_block_mutation();
CREATE TRIGGER events_block_delete    BEFORE DELETE ON events    FOR EACH ROW EXECUTE FUNCTION workstream_block_mutation();
CREATE TRIGGER verdicts_block_update  BEFORE UPDATE ON verdicts  FOR EACH ROW EXECUTE FUNCTION workstream_block_mutation();
CREATE TRIGGER verdicts_block_delete  BEFORE DELETE ON verdicts  FOR EACH ROW EXECUTE FUNCTION workstream_block_mutation();

--==============================================================================
-- Automatic event emission triggers (§4.5)
--==============================================================================

-- On INSERT of task/finding/attempt: write a `created` event.
CREATE OR REPLACE FUNCTION workstream_emit_created_event() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    v_actor_id uuid;
BEGIN
    v_actor_id := CASE TG_TABLE_NAME
        WHEN 'attempts' THEN NEW.actor_id
        ELSE NULL
    END;

    INSERT INTO events (actor_id, entity_type, entity_id, event_type, to_state, payload)
    VALUES (
        v_actor_id,
        CASE TG_TABLE_NAME
            WHEN 'tasks'    THEN 'task'
            WHEN 'findings' THEN 'finding'
            WHEN 'attempts' THEN 'attempt'
        END,
        NEW.id,
        'created',
        CASE TG_TABLE_NAME
            WHEN 'attempts' THEN NULL
            ELSE NEW.status
        END,
        '{}'::jsonb
    );
    RETURN NEW;
END;
$$;

CREATE TRIGGER tasks_emit_created    AFTER INSERT ON tasks    FOR EACH ROW EXECUTE FUNCTION workstream_emit_created_event();
CREATE TRIGGER findings_emit_created AFTER INSERT ON findings FOR EACH ROW EXECUTE FUNCTION workstream_emit_created_event();
CREATE TRIGGER attempts_emit_created AFTER INSERT ON attempts FOR EACH ROW EXECUTE FUNCTION workstream_emit_created_event();

-- On UPDATE of task/finding: when status changes, write a `status_changed` event
-- and bump updated_at. Application-emitted events still happen alongside; this
-- is a backstop so no transition can silently slip past.
CREATE OR REPLACE FUNCTION workstream_emit_status_change_event() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    IF NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
        VALUES (
            COALESCE(NEW.claim_actor_id, NEW.assignee_actor_id, NULL),
            CASE TG_TABLE_NAME
                WHEN 'tasks'    THEN 'task'
                WHEN 'findings' THEN 'finding'
            END,
            NEW.id,
            'status_changed',
            OLD.status,
            NEW.status,
            jsonb_build_object('source','trigger')
        );
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER tasks_status_change
    BEFORE UPDATE ON tasks
    FOR EACH ROW EXECUTE FUNCTION workstream_emit_status_change_event();

-- findings trigger references claim_actor_id but not assignee_actor_id (no such col)
CREATE OR REPLACE FUNCTION workstream_findings_status_change() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.updated_at := now();
    IF NEW.status IS DISTINCT FROM OLD.status THEN
        INSERT INTO events (actor_id, entity_type, entity_id, event_type, from_state, to_state, payload)
        VALUES (
            NEW.claim_actor_id,
            'finding',
            NEW.id,
            'status_changed',
            OLD.status,
            NEW.status,
            jsonb_build_object('source','trigger')
        );
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER findings_status_change
    BEFORE UPDATE ON findings
    FOR EACH ROW EXECUTE FUNCTION workstream_findings_status_change();

--==============================================================================
-- LISTEN/NOTIFY channel for in-process dashboard cache invalidation (§9.1)
--==============================================================================

CREATE OR REPLACE FUNCTION workstream_notify_plan_changed() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF TG_TABLE_NAME = 'tasks' THEN
        PERFORM pg_notify('workstream_plan_changed', NEW.plan_id::text);
    ELSIF TG_TABLE_NAME = 'findings' THEN
        PERFORM pg_notify('workstream_plan_changed', (
            SELECT plan_id::text FROM tasks WHERE id = NEW.task_id
        ));
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER tasks_notify_plan_changed
    AFTER INSERT OR UPDATE ON tasks
    FOR EACH ROW EXECUTE FUNCTION workstream_notify_plan_changed();

CREATE TRIGGER findings_notify_plan_changed
    AFTER INSERT OR UPDATE ON findings
    FOR EACH ROW EXECUTE FUNCTION workstream_notify_plan_changed();

--==============================================================================
-- Migration bookkeeping
--==============================================================================

CREATE TABLE IF NOT EXISTS __migrations (
    version     text PRIMARY KEY,
    applied_at  timestamptz NOT NULL DEFAULT now(),
    checksum    text
);

INSERT INTO __migrations (version) VALUES ('0001_initial')
ON CONFLICT (version) DO NOTHING;

COMMIT;
