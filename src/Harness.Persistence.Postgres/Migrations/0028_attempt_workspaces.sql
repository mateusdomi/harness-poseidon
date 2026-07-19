CREATE TABLE harness.attempt_workspaces
(
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    task_id char(26) NOT NULL,
    attempt_id char(26) NOT NULL,
    technical_execution_id char(26) NULL,
    repository_root text NOT NULL,
    controlled_root text NOT NULL,
    base_reference text NOT NULL,
    branch_name text NOT NULL CHECK (branch_name LIKE 'task/%'),
    worktree_path text NOT NULL,
    state varchar(20) NOT NULL
        CHECK (state IN ('claimed', 'prepared', 'running', 'completed', 'failed')),
    cleanup_state varchar(20) NOT NULL
        CHECK (cleanup_state IN ('not_required', 'pending', 'completed')),
    owner varchar(200) NOT NULL CHECK (length(owner) > 0),
    fencing_token bigint NOT NULL CHECK (fencing_token > 0),
    lease_expires_at timestamptz NOT NULL,
    last_heartbeat_at timestamptz NOT NULL,
    commit_sha char(40) NULL,
    session_id text NULL,
    final_error varchar(2000) NULL CHECK (final_error IS NULL OR length(final_error) > 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    released_at timestamptz NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (tenant_id, attempt_id),
    UNIQUE (tenant_id, worktree_path),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES harness.work_attempts(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, task_id)
        REFERENCES harness.work_tasks(tenant_id, project_id, id),
    CHECK ((cleanup_state = 'completed') = (released_at IS NOT NULL)),
    CHECK ((cleanup_state = 'not_required') = (state IN ('claimed', 'prepared', 'running'))),
    CHECK (final_error IS NULL OR state = 'failed')
);

CREATE UNIQUE INDEX ux_attempt_workspaces_active_branch
    ON harness.attempt_workspaces (tenant_id, repository_root, branch_name)
    WHERE released_at IS NULL;

CREATE INDEX ix_attempt_workspaces_expired
    ON harness.attempt_workspaces (tenant_id, lease_expires_at)
    WHERE released_at IS NULL;

CREATE TABLE harness.attempt_scope_claims
(
    id char(26) NOT NULL UNIQUE,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    attempt_id char(26) NOT NULL,
    path_pattern text NOT NULL CHECK (length(path_pattern) > 0),
    released_at timestamptz NULL,
    PRIMARY KEY (tenant_id, attempt_id, path_pattern),
    FOREIGN KEY (tenant_id, attempt_id)
        REFERENCES harness.attempt_workspaces(tenant_id, attempt_id)
);

CREATE INDEX ix_attempt_scope_claims_active
    ON harness.attempt_scope_claims (tenant_id, project_id, released_at, attempt_id, path_pattern);
