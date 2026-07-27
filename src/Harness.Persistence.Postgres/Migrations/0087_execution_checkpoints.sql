-- Paridade com o SQLite: o estado recuperável pertence ao card, não à tentativa nem ao ator.
CREATE TABLE harness.execution_checkpoints
(
    tenant_id text NOT NULL,
    checkpoint_id text NOT NULL CHECK (length(checkpoint_id) = 26),
    project_id text NOT NULL,
    task_id text NOT NULL,
    execution_id text NOT NULL,
    source_attempt_id text NOT NULL,
    source_account_alias text NOT NULL,
    source_role text NOT NULL,
    origin text NOT NULL CHECK (origin IN ('quota','transient','cancelled','review')),
    branch_name text NOT NULL,
    source_commit text NULL,
    repository_root text NOT NULL,
    scope_claims_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    changed_files_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    progress_note text NULL CHECK (progress_note IS NULL OR length(progress_note) <= 8000),
    pending_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    evidence_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    origin_fencing_token bigint NOT NULL DEFAULT 0,
    consumed_by_attempt_id text NULL,
    consumed_at timestamptz NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, checkpoint_id)
);

CREATE UNIQUE INDEX ux_execution_checkpoints_available
    ON harness.execution_checkpoints (tenant_id, task_id)
    WHERE consumed_by_attempt_id IS NULL;

CREATE INDEX ix_execution_checkpoints_task
    ON harness.execution_checkpoints (tenant_id, task_id, created_at);

ALTER TABLE harness.execution_checkpoints ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.execution_checkpoints FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.execution_checkpoints
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
