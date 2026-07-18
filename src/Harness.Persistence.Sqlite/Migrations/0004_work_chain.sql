CREATE UNIQUE INDEX ux_projects_tenant_id ON projects (tenant_id, id);
CREATE UNIQUE INDEX ux_local_users_tenant_id ON local_users (tenant_id, id);

CREATE TABLE solicitations
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    user_id TEXT NOT NULL,
    content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 20000),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES local_users(tenant_id, id)
);

CREATE TABLE demands
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    solicitation_id TEXT NOT NULL,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    acceptance_criteria_json TEXT NOT NULL
        CHECK (json_valid(acceptance_criteria_json) AND json_type(acceptance_criteria_json) = 'array'),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, solicitation_id)
        REFERENCES solicitations(tenant_id, project_id, id)
);

CREATE TABLE work_tasks
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    demand_id TEXT NOT NULL,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    risk_tier TEXT NOT NULL CHECK (risk_tier IN ('low', 'medium', 'high', 'critical')),
    weight REAL NOT NULL CHECK (weight > 0),
    state TEXT NOT NULL CHECK (state IN ('ready', 'running', 'awaiting_review', 'completed')),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, demand_id)
        REFERENCES demands(tenant_id, project_id, id)
);

CREATE TABLE instruction_versions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version > 0),
    content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 100000),
    content_hash TEXT NOT NULL CHECK (length(content_hash) = 64),
    supersedes_id TEXT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (task_id, version),
    UNIQUE (task_id, id),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, task_id)
        REFERENCES work_tasks(tenant_id, project_id, id),
    FOREIGN KEY (task_id, supersedes_id) REFERENCES instruction_versions(task_id, id)
);

CREATE TABLE work_attempts
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    instruction_version_id TEXT NOT NULL,
    attempt_number INTEGER NOT NULL CHECK (attempt_number > 0),
    producer_agent_id TEXT NOT NULL CHECK (length(producer_agent_id) BETWEEN 1 AND 200),
    state TEXT NOT NULL CHECK (state IN ('running', 'awaiting_review', 'approved', 'rejected')),
    started_at TEXT NOT NULL,
    completed_at TEXT NULL,
    UNIQUE (task_id, attempt_number),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, task_id)
        REFERENCES work_tasks(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, instruction_version_id)
        REFERENCES instruction_versions(tenant_id, project_id, id)
);

CREATE UNIQUE INDEX ux_work_attempts_one_active
    ON work_attempts (task_id) WHERE state = 'running';

CREATE TABLE work_evidence
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    ordinal INTEGER NOT NULL CHECK (ordinal > 0),
    reference TEXT NOT NULL CHECK (length(reference) BETWEEN 1 AND 2000),
    created_at TEXT NOT NULL,
    UNIQUE (attempt_id, ordinal),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES work_attempts(tenant_id, project_id, id)
);

CREATE TABLE work_reviews
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    reviewer_agent_id TEXT NOT NULL CHECK (length(reviewer_agent_id) BETWEEN 1 AND 200),
    decision TEXT NOT NULL CHECK (decision IN ('approved', 'rejected')),
    rationale TEXT NOT NULL CHECK (length(rationale) BETWEEN 1 AND 10000),
    created_at TEXT NOT NULL,
    UNIQUE (attempt_id),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES work_attempts(tenant_id, project_id, id)
);

CREATE INDEX ix_solicitations_project_created ON solicitations (tenant_id, project_id, created_at, id);
CREATE INDEX ix_demands_solicitation ON demands (solicitation_id, created_at, id);
CREATE INDEX ix_work_tasks_demand_state ON work_tasks (demand_id, state, created_at, id);
CREATE INDEX ix_instruction_versions_task ON instruction_versions (task_id, version);
CREATE INDEX ix_work_attempts_task_state ON work_attempts (task_id, state, attempt_number);
CREATE INDEX ix_work_evidence_attempt ON work_evidence (attempt_id, ordinal);
