CREATE TABLE harness.context_snapshots
(
    tenant_id text NOT NULL,
    snapshot_id text NOT NULL,
    project_id text NOT NULL,
    work_task_id text NOT NULL,
    execution_id text NOT NULL,
    manifest_version text NOT NULL,
    bundle_manifest_ids_json jsonb NOT NULL,
    sources_json jsonb NOT NULL,
    assembled_context_hash text NOT NULL,
    token_count integer NOT NULL CHECK (token_count >= 0),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, snapshot_id)
);

CREATE INDEX ix_context_snapshots_execution
    ON harness.context_snapshots (tenant_id, project_id, execution_id);

ALTER TABLE harness.context_snapshots ENABLE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.context_snapshots
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
