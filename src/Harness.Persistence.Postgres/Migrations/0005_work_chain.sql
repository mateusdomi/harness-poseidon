CREATE UNIQUE INDEX ux_projects_tenant_id ON harness.projects (tenant_id, id);
CREATE UNIQUE INDEX ux_local_users_tenant_id ON harness.local_users (tenant_id, id);

CREATE TABLE harness.solicitations
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    user_id char(26) NOT NULL,
    content varchar(20000) NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, user_id) REFERENCES harness.local_users(tenant_id, id),
    CHECK (length(trim(content)) > 0)
);

CREATE TABLE harness.demands
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    solicitation_id char(26) NOT NULL,
    title varchar(500) NOT NULL,
    acceptance_criteria_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, solicitation_id)
        REFERENCES harness.solicitations(tenant_id, project_id, id),
    CHECK (length(trim(title)) > 0),
    CHECK (jsonb_typeof(acceptance_criteria_json) = 'array')
);

CREATE TABLE harness.work_tasks
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    demand_id char(26) NOT NULL,
    title varchar(500) NOT NULL,
    risk_tier varchar(20) NOT NULL CHECK (risk_tier IN ('low', 'medium', 'high', 'critical')),
    weight numeric(18,6) NOT NULL CHECK (weight > 0),
    state varchar(30) NOT NULL CHECK (state IN ('ready', 'running', 'awaiting_review', 'completed')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, demand_id)
        REFERENCES harness.demands(tenant_id, project_id, id),
    CHECK (length(trim(title)) > 0)
);

CREATE TABLE harness.instruction_versions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    task_id char(26) NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    content varchar(100000) NOT NULL,
    content_hash char(64) NOT NULL,
    supersedes_id char(26) NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (task_id, version),
    UNIQUE (task_id, id),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, task_id)
        REFERENCES harness.work_tasks(tenant_id, project_id, id),
    FOREIGN KEY (task_id, supersedes_id)
        REFERENCES harness.instruction_versions(task_id, id),
    CHECK (length(trim(content)) > 0)
);

CREATE TABLE harness.work_attempts
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    task_id char(26) NOT NULL,
    instruction_version_id char(26) NOT NULL,
    attempt_number integer NOT NULL CHECK (attempt_number > 0),
    producer_agent_id varchar(200) NOT NULL,
    state varchar(30) NOT NULL CHECK (state IN ('running', 'awaiting_review', 'approved', 'rejected')),
    started_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    UNIQUE (task_id, attempt_number),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, task_id)
        REFERENCES harness.work_tasks(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, instruction_version_id)
        REFERENCES harness.instruction_versions(tenant_id, project_id, id),
    CHECK (length(trim(producer_agent_id)) > 0)
);

CREATE UNIQUE INDEX ux_work_attempts_one_active
    ON harness.work_attempts (task_id) WHERE state = 'running';

CREATE TABLE harness.work_evidence
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    attempt_id char(26) NOT NULL,
    ordinal integer NOT NULL CHECK (ordinal > 0),
    reference varchar(2000) NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (attempt_id, ordinal),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES harness.work_attempts(tenant_id, project_id, id),
    CHECK (length(trim(reference)) > 0)
);

CREATE TABLE harness.work_reviews
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    attempt_id char(26) NOT NULL,
    reviewer_agent_id varchar(200) NOT NULL,
    decision varchar(20) NOT NULL CHECK (decision IN ('approved', 'rejected')),
    rationale varchar(10000) NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (attempt_id),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES harness.work_attempts(tenant_id, project_id, id),
    CHECK (length(trim(reviewer_agent_id)) > 0),
    CHECK (length(trim(rationale)) > 0)
);

CREATE INDEX ix_solicitations_project_created ON harness.solicitations (tenant_id, project_id, created_at, id);
CREATE INDEX ix_demands_solicitation ON harness.demands (solicitation_id, created_at, id);
CREATE INDEX ix_work_tasks_demand_state ON harness.work_tasks (demand_id, state, created_at, id);
CREATE INDEX ix_instruction_versions_task ON harness.instruction_versions (task_id, version);
CREATE INDEX ix_work_attempts_task_state ON harness.work_attempts (task_id, state, attempt_number);
CREATE INDEX ix_work_evidence_attempt ON harness.work_evidence (attempt_id, ordinal);
