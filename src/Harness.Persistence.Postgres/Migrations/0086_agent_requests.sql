-- Paridade com o SQLite: solicitação estruturada de um agente à chefe (inclui expansão de escopo).
CREATE TABLE harness.agent_requests
(
    tenant_id text NOT NULL,
    request_id text NOT NULL CHECK (length(request_id) = 26),
    project_id text NOT NULL,
    task_id text NOT NULL,
    attempt_id text NULL,
    kind text NOT NULL CHECK (kind IN
        ('needs_decision','needs_clarification','blocked_by_dependency',
         'scope_expansion','external_resource','canonical_conflict')),
    question text NOT NULL CHECK (length(question) BETWEEN 1 AND 4000),
    reason text NOT NULL CHECK (length(reason) BETWEEN 1 AND 4000),
    options_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    recommended_option text NULL,
    evidence_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    requested_paths_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    blocking boolean NOT NULL DEFAULT true,
    state text NOT NULL DEFAULT 'open' CHECK (state IN
        ('open','answered','rejected','escalated','superseded')),
    answered_by text NULL CHECK (answered_by IS NULL OR answered_by IN ('chief','human')),
    answer text NULL CHECK (answer IS NULL OR length(answer) <= 8000),
    answer_reason_code text NULL,
    fencing_token bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    answered_at timestamptz NULL,
    PRIMARY KEY (tenant_id, request_id)
);

CREATE INDEX ix_agent_requests_open
    ON harness.agent_requests (tenant_id, project_id, state, created_at);

CREATE INDEX ix_agent_requests_task
    ON harness.agent_requests (tenant_id, task_id, state);

CREATE UNIQUE INDEX ux_agent_requests_open_per_attempt
    ON harness.agent_requests (tenant_id, task_id, attempt_id, kind, question)
    WHERE state = 'open';

ALTER TABLE harness.agent_requests ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.agent_requests FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.agent_requests
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
