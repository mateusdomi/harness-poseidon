CREATE TABLE governance_turn_receipts
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    manifest_version TEXT NOT NULL,
    documents_json TEXT NOT NULL CHECK (json_valid(documents_json)),
    estimated_tokens INTEGER NOT NULL CHECK (estimated_tokens >= 0),
    actual_prompt_tokens INTEGER NULL CHECK (actual_prompt_tokens IS NULL OR actual_prompt_tokens >= 0),
    truncated_json TEXT NOT NULL CHECK (json_valid(truncated_json)),
    conflicts_json TEXT NOT NULL CHECK (json_valid(conflicts_json)),
    cache_hits INTEGER NOT NULL CHECK (cache_hits >= 0),
    provider TEXT NOT NULL,
    model TEXT NULL,
    occurred_at TEXT NOT NULL,
    bundle_checksum TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('selected','delivered','completed','failed')),
    gate_result TEXT NULL,
    version INTEGER NOT NULL CHECK (version >= 1),
    PRIMARY KEY (tenant_id, turn_id)
);

CREATE INDEX ix_governance_receipts_project
    ON governance_turn_receipts (tenant_id, project_id, turn_id);

CREATE TABLE governance_receipt_metrics
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    event_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN
        ('selected','delivered','opened_by_tool','rule_triggered','violation_detected',
         'item_truncated','gate_result','patch_applied','patch_rejected','evaluator_verdict')),
    document_id TEXT NULL,
    rule_id TEXT NULL,
    detail_code TEXT NULL,
    token_count INTEGER NULL CHECK (token_count IS NULL OR token_count >= 0),
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, event_id),
    FOREIGN KEY (tenant_id, turn_id)
        REFERENCES governance_turn_receipts (tenant_id, turn_id) ON DELETE RESTRICT
);

CREATE INDEX ix_governance_metrics_turn
    ON governance_receipt_metrics (tenant_id, turn_id, occurred_at, event_id);
