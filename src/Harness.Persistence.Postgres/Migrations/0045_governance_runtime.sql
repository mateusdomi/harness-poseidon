CREATE TABLE harness.governance_turn_receipts
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    task_id text NOT NULL,
    attempt_id text NOT NULL,
    turn_id text NOT NULL,
    agent_id text NOT NULL,
    manifest_version text NOT NULL,
    documents_json jsonb NOT NULL,
    estimated_tokens integer NOT NULL CHECK (estimated_tokens >= 0),
    actual_prompt_tokens integer NULL CHECK (actual_prompt_tokens IS NULL OR actual_prompt_tokens >= 0),
    truncated_json jsonb NOT NULL,
    conflicts_json jsonb NOT NULL,
    cache_hits integer NOT NULL CHECK (cache_hits >= 0),
    provider text NOT NULL,
    model text NULL,
    occurred_at timestamptz NOT NULL,
    bundle_checksum text NOT NULL,
    state text NOT NULL CHECK (state IN ('selected','delivered','completed','failed')),
    gate_result text NULL,
    version bigint NOT NULL CHECK (version >= 1),
    PRIMARY KEY (tenant_id, turn_id)
);

CREATE INDEX ix_governance_receipts_project
    ON harness.governance_turn_receipts (tenant_id, project_id, turn_id);

CREATE TABLE harness.governance_receipt_metrics
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    turn_id text NOT NULL,
    event_id text NOT NULL,
    kind text NOT NULL CHECK (kind IN
        ('selected','delivered','opened_by_tool','rule_triggered','violation_detected',
         'item_truncated','gate_result','patch_applied','patch_rejected','evaluator_verdict')),
    document_id text NULL,
    rule_id text NULL,
    detail_code text NULL,
    token_count integer NULL CHECK (token_count IS NULL OR token_count >= 0),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, event_id),
    FOREIGN KEY (tenant_id, turn_id)
        REFERENCES harness.governance_turn_receipts (tenant_id, turn_id) ON DELETE RESTRICT
);

CREATE INDEX ix_governance_metrics_turn
    ON harness.governance_receipt_metrics (tenant_id, turn_id, occurred_at, event_id);
