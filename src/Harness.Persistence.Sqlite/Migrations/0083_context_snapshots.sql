CREATE TABLE context_snapshots
(
    tenant_id TEXT NOT NULL,
    snapshot_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    work_task_id TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    manifest_version TEXT NOT NULL,
    bundle_manifest_ids_json TEXT NOT NULL CHECK (json_valid(bundle_manifest_ids_json)),
    sources_json TEXT NOT NULL CHECK (json_valid(sources_json)),
    assembled_context_hash TEXT NOT NULL,
    token_count INTEGER NOT NULL CHECK (token_count >= 0),
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, snapshot_id)
);

CREATE INDEX ix_context_snapshots_execution
    ON context_snapshots (tenant_id, project_id, execution_id);
