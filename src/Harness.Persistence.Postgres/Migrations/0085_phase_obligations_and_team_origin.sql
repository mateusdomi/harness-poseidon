-- Paridade com o SQLite: obrigações reais da fase + procedência/ciclo de vida das personas.
CREATE TABLE harness.phase_obligations
(
    tenant_id text NOT NULL,
    obligation_id text NOT NULL CHECK (length(obligation_id) = 26),
    project_id text NOT NULL,
    run_id text NOT NULL,
    phase_key text NOT NULL CHECK (length(phase_key) BETWEEN 1 AND 200),
    obligation_key text NOT NULL CHECK (length(obligation_key) BETWEEN 1 AND 200),
    plan_version integer NOT NULL CHECK (plan_version >= 1),
    kind text NOT NULL CHECK (kind IN
        ('document','implementation','test','review','evidence','metric','decision','operation')),
    description text NOT NULL CHECK (length(description) BETWEEN 1 AND 2000),
    required boolean NOT NULL DEFAULT true,
    weight double precision NOT NULL DEFAULT 1 CHECK (weight >= 0),
    source text NOT NULL CHECK (source IN ('template','plan','chief','policy')),
    completion_criteria text NULL CHECK (completion_criteria IS NULL OR length(completion_criteria) <= 2000),
    card_id text NULL,
    objective_key text NULL,
    artifact_ref text NULL,
    state text NOT NULL DEFAULT 'pending' CHECK (state IN
        ('pending','in_progress','in_review','blocked','accepted','cancelled')),
    evidence_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    reason text NULL CHECK (reason IS NULL OR length(reason) <= 2000),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, obligation_id),
    UNIQUE (tenant_id, run_id, phase_key, plan_version, obligation_key)
);

CREATE INDEX ix_phase_obligations_phase
    ON harness.phase_obligations (tenant_id, run_id, phase_key, plan_version);

CREATE INDEX ix_phase_obligations_card
    ON harness.phase_obligations (tenant_id, card_id);

ALTER TABLE harness.phase_obligations ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.phase_obligations FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.phase_obligations
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));

ALTER TABLE harness.agent_definitions ADD COLUMN origin text NOT NULL DEFAULT 'human'
    CHECK (origin IN ('human','chief','system'));

ALTER TABLE harness.agent_definitions ADD COLUMN lifecycle_state text NOT NULL DEFAULT 'active'
    CHECK (lifecycle_state IN ('project_scoped','active','reusable','global','observation','quarantined','disabled'));

ALTER TABLE harness.agent_definitions ADD COLUMN scope_project_id text NULL;

ALTER TABLE harness.agent_definitions ADD COLUMN creation_reason text NULL
    CHECK (creation_reason IS NULL OR length(creation_reason) <= 2000);

CREATE INDEX ix_agent_definitions_origin
    ON harness.agent_definitions (tenant_id, origin, lifecycle_state);
