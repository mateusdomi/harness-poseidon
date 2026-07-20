CREATE TABLE harness.learning_candidates
(
    tenant_id text NOT NULL,
    organization_id text NOT NULL,
    project_id text NOT NULL,
    candidate_id text NOT NULL,
    type text NOT NULL CHECK (type IN
        ('rule','skill','persona_refinement','workflow_refinement','tool_routing_recommendation',
         'documentation_correction','provider_model_routing_recommendation')),
    state text NOT NULL CHECK (state IN
        ('candidate','in_review','awaiting_evaluation','evaluated','shadow','approved','rejected','promoted',
         'rolled_back','deprecated')),
    fingerprint text NOT NULL CHECK (length(fingerprint) = 64),
    observation text NOT NULL,
    evidence_json jsonb NOT NULL,
    payload_json jsonb NOT NULL,
    actor_agent_id text NOT NULL,
    actor_provider text NOT NULL,
    actor_model text NULL,
    baseline_version text NOT NULL,
    proposed_version text NOT NULL,
    evaluator_agent_id text NULL,
    evaluator_provider text NULL,
    evaluator_model text NULL,
    evaluation_verdict text NULL,
    shadow_result_json jsonb NULL,
    reviewer_profile_id text NULL,
    decision_note text NULL,
    active_version text NULL,
    previous_version text NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    version bigint NOT NULL CHECK (version >= 1),
    PRIMARY KEY (tenant_id, candidate_id),
    FOREIGN KEY (project_id) REFERENCES harness.projects(id) ON DELETE RESTRICT,
    UNIQUE (tenant_id, project_id, type, fingerprint)
);

CREATE INDEX ix_learning_candidates_list
    ON harness.learning_candidates (tenant_id, organization_id, project_id, state, candidate_id);

CREATE TABLE harness.learning_candidate_history
(
    tenant_id text NOT NULL,
    event_id text NOT NULL,
    candidate_id text NOT NULL,
    from_state text NOT NULL,
    to_state text NOT NULL,
    action text NULL,
    actor_id text NOT NULL,
    note text NULL,
    occurred_at timestamptz NOT NULL,
    candidate_version bigint NOT NULL CHECK (candidate_version >= 1),
    PRIMARY KEY (tenant_id, event_id),
    FOREIGN KEY (tenant_id, candidate_id)
        REFERENCES harness.learning_candidates (tenant_id, candidate_id) ON DELETE RESTRICT
);

CREATE INDEX ix_learning_candidate_history
    ON harness.learning_candidate_history (tenant_id, candidate_id, occurred_at, event_id);

CREATE TABLE harness.learning_candidate_metrics
(
    tenant_id text NOT NULL,
    metric_id text NOT NULL,
    organization_id text NOT NULL,
    project_id text NOT NULL,
    candidate_id text NOT NULL,
    kind text NOT NULL CHECK (kind IN
        ('created','deduplicated','rejected','approved','promoted','rolled_back')),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, metric_id),
    FOREIGN KEY (tenant_id, candidate_id)
        REFERENCES harness.learning_candidates (tenant_id, candidate_id) ON DELETE RESTRICT
);

CREATE INDEX ix_learning_candidate_metrics_scope
    ON harness.learning_candidate_metrics (tenant_id, organization_id, project_id, kind, occurred_at);
