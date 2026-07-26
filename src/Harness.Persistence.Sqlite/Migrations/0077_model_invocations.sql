CREATE TABLE IF NOT EXISTS model_invocations (
    id TEXT PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    work_task_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    provider TEXT NOT NULL,
    model TEXT NOT NULL,
    account_alias TEXT NOT NULL,
    input_tokens INTEGER NOT NULL DEFAULT 0,
    output_tokens INTEGER NOT NULL DEFAULT 0,
    estimated_cost_usd REAL NOT NULL DEFAULT 0.0,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    outcome TEXT NOT NULL,
    invoked_at TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_model_invocations_tenant_task ON model_invocations(tenant_id, work_task_id);
CREATE INDEX IF NOT EXISTS idx_model_invocations_tenant_project ON model_invocations(tenant_id, project_id);
