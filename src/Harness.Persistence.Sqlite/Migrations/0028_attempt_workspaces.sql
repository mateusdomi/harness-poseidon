CREATE TABLE attempt_workspaces
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL CHECK (length(attempt_id) = 26),
    technical_execution_id TEXT NULL
        CHECK (technical_execution_id IS NULL OR length(technical_execution_id) = 26),
    repository_root TEXT NOT NULL,
    controlled_root TEXT NOT NULL,
    base_reference TEXT NOT NULL,
    branch_name TEXT NOT NULL CHECK (branch_name LIKE 'task/%'),
    worktree_path TEXT NOT NULL,
    state TEXT NOT NULL
        CHECK (state IN ('claimed', 'prepared', 'running', 'completed', 'failed')),
    cleanup_state TEXT NOT NULL
        CHECK (cleanup_state IN ('not_required', 'pending', 'completed')),
    owner TEXT NOT NULL CHECK (length(owner) BETWEEN 1 AND 200),
    fencing_token INTEGER NOT NULL CHECK (fencing_token > 0),
    lease_expires_at TEXT NOT NULL,
    last_heartbeat_at TEXT NOT NULL,
    commit_sha TEXT NULL CHECK (commit_sha IS NULL OR length(commit_sha) = 40),
    session_id TEXT NULL,
    final_error TEXT NULL CHECK (final_error IS NULL OR length(final_error) BETWEEN 1 AND 2000),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    released_at TEXT NULL,
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (tenant_id, attempt_id),
    UNIQUE (tenant_id, worktree_path),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES work_attempts(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, task_id)
        REFERENCES work_tasks(tenant_id, project_id, id),
    CHECK ((cleanup_state = 'completed') = (released_at IS NOT NULL)),
    CHECK ((cleanup_state = 'not_required') = (state IN ('claimed', 'prepared', 'running'))),
    CHECK (final_error IS NULL OR state = 'failed')
);

CREATE UNIQUE INDEX ux_attempt_workspaces_active_branch
    ON attempt_workspaces (tenant_id, repository_root, branch_name)
    WHERE released_at IS NULL;

CREATE INDEX ix_attempt_workspaces_expired
    ON attempt_workspaces (tenant_id, lease_expires_at)
    WHERE released_at IS NULL;

CREATE TABLE attempt_scope_claims
(
    id TEXT NOT NULL UNIQUE CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    path_pattern TEXT NOT NULL CHECK (length(path_pattern) > 0),
    released_at TEXT NULL,
    PRIMARY KEY (tenant_id, attempt_id, path_pattern),
    FOREIGN KEY (tenant_id, attempt_id)
        REFERENCES attempt_workspaces(tenant_id, attempt_id)
);

CREATE INDEX ix_attempt_scope_claims_active
    ON attempt_scope_claims (tenant_id, project_id, released_at, attempt_id, path_pattern);
