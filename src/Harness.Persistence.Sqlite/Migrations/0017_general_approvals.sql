CREATE TABLE general_approval_requests
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    gate_id TEXT NULL CHECK (gate_id IS NULL OR length(gate_id) = 26),
    task_id TEXT NULL CHECK (task_id IS NULL OR length(task_id) = 26),
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 10000),
    priority TEXT NOT NULL CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    due_at TEXT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending', 'approved', 'rejected', 'cancelled')),
    requested_by_agent_id TEXT NOT NULL CHECK (length(requested_by_agent_id) = 26),
    requested_at TEXT NOT NULL,
    resolved_by_profile_id TEXT NULL CHECK
        (resolved_by_profile_id IS NULL OR length(resolved_by_profile_id) = 26),
    resolved_at TEXT NULL,
    resolution_note TEXT NULL CHECK
        (resolution_note IS NULL OR length(resolution_note) BETWEEN 1 AND 10000),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    CHECK (gate_id IS NULL OR task_id IS NULL),
    CHECK ((state = 'pending' AND resolved_by_profile_id IS NULL AND resolved_at IS NULL
            AND resolution_note IS NULL) OR
           (state = 'approved' AND resolved_by_profile_id IS NOT NULL AND resolved_at IS NOT NULL) OR
           (state = 'rejected' AND resolved_by_profile_id IS NOT NULL AND resolved_at IS NOT NULL
            AND resolution_note IS NOT NULL) OR
           (state = 'cancelled' AND resolved_at IS NOT NULL AND resolution_note IS NOT NULL))
);

CREATE INDEX ix_general_approval_queue
    ON general_approval_requests (tenant_id, project_id, state, due_at, priority, requested_at, id);
