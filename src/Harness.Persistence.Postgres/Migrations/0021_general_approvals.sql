CREATE TABLE harness.general_approval_requests
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    gate_id char(26) NULL,
    task_id char(26) NULL,
    title varchar(500) NOT NULL CHECK (length(title) > 0),
    description varchar(10000) NOT NULL CHECK (length(description) > 0),
    priority varchar(20) NOT NULL CHECK (priority IN ('low', 'medium', 'high', 'critical')),
    due_at timestamptz NULL,
    state varchar(20) NOT NULL CHECK (state IN ('pending', 'approved', 'rejected', 'cancelled')),
    requested_by_agent_id char(26) NOT NULL,
    requested_at timestamptz NOT NULL,
    resolved_by_profile_id char(26) NULL,
    resolved_at timestamptz NULL,
    resolution_note varchar(10000) NULL CHECK
        (resolution_note IS NULL OR length(resolution_note) > 0),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    CHECK (gate_id IS NULL OR task_id IS NULL),
    CHECK ((state = 'pending' AND resolved_by_profile_id IS NULL AND resolved_at IS NULL
            AND resolution_note IS NULL) OR
           (state = 'approved' AND resolved_by_profile_id IS NOT NULL AND resolved_at IS NOT NULL) OR
           (state = 'rejected' AND resolved_by_profile_id IS NOT NULL AND resolved_at IS NOT NULL
            AND resolution_note IS NOT NULL) OR
           (state = 'cancelled' AND resolved_at IS NOT NULL AND resolution_note IS NOT NULL))
);

CREATE INDEX ix_general_approval_queue
    ON harness.general_approval_requests
       (tenant_id, project_id, state, due_at, priority, requested_at, id);
