CREATE TABLE learning_candidates
(
    tenant_id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    candidate_id TEXT NOT NULL,
    type TEXT NOT NULL CHECK (type IN
        ('rule','skill','persona_refinement','workflow_refinement','tool_routing_recommendation',
         'documentation_correction','provider_model_routing_recommendation')),
    state TEXT NOT NULL CHECK (state IN
        ('candidate','in_review','awaiting_evaluation','evaluated','shadow','approved','rejected','promoted',
         'rolled_back','deprecated')),
    fingerprint TEXT NOT NULL CHECK (length(fingerprint) = 64),
    observation TEXT NOT NULL,
    evidence_json TEXT NOT NULL CHECK (json_valid(evidence_json)),
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    actor_agent_id TEXT NOT NULL,
    actor_provider TEXT NOT NULL,
    actor_model TEXT NULL,
    baseline_version TEXT NOT NULL,
    proposed_version TEXT NOT NULL,
    evaluator_agent_id TEXT NULL,
    evaluator_provider TEXT NULL,
    evaluator_model TEXT NULL,
    evaluation_verdict TEXT NULL,
    shadow_result_json TEXT NULL CHECK (shadow_result_json IS NULL OR json_valid(shadow_result_json)),
    reviewer_profile_id TEXT NULL,
    decision_note TEXT NULL,
    active_version TEXT NULL,
    previous_version TEXT NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    PRIMARY KEY (tenant_id, candidate_id),
    FOREIGN KEY (project_id) REFERENCES projects(id) ON DELETE RESTRICT,
    UNIQUE (tenant_id, project_id, type, fingerprint)
);

CREATE INDEX ix_learning_candidates_list
    ON learning_candidates (tenant_id, organization_id, project_id, state, candidate_id);

CREATE TABLE learning_candidate_history
(
    tenant_id TEXT NOT NULL,
    event_id TEXT NOT NULL,
    candidate_id TEXT NOT NULL,
    from_state TEXT NOT NULL,
    to_state TEXT NOT NULL,
    action TEXT NULL,
    actor_id TEXT NOT NULL,
    note TEXT NULL,
    occurred_at TEXT NOT NULL,
    candidate_version INTEGER NOT NULL CHECK (candidate_version >= 1),
    PRIMARY KEY (tenant_id, event_id),
    FOREIGN KEY (tenant_id, candidate_id)
        REFERENCES learning_candidates (tenant_id, candidate_id) ON DELETE RESTRICT
);

CREATE INDEX ix_learning_candidate_history
    ON learning_candidate_history (tenant_id, candidate_id, occurred_at, event_id);

CREATE TABLE learning_candidate_metrics
(
    tenant_id TEXT NOT NULL,
    metric_id TEXT NOT NULL,
    organization_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    candidate_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN
        ('created','deduplicated','rejected','approved','promoted','rolled_back')),
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, metric_id),
    FOREIGN KEY (tenant_id, candidate_id)
        REFERENCES learning_candidates (tenant_id, candidate_id) ON DELETE RESTRICT
);

CREATE INDEX ix_learning_candidate_metrics_scope
    ON learning_candidate_metrics (tenant_id, organization_id, project_id, kind, occurred_at);
