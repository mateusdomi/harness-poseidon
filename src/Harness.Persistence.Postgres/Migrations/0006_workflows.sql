CREATE TABLE harness.workflow_definitions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    name varchar(200) NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id) REFERENCES harness.tenants(id),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.workflow_definition_versions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    definition_id char(26) NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    status varchar(20) NOT NULL CHECK (status IN ('draft', 'published')),
    content_hash char(64) NOT NULL,
    created_at timestamptz NOT NULL,
    published_at timestamptz NULL,
    UNIQUE (definition_id, version),
    UNIQUE (tenant_id, id),
    UNIQUE (id, definition_id),
    FOREIGN KEY (tenant_id, definition_id)
        REFERENCES harness.workflow_definitions(tenant_id, id),
    CHECK ((status = 'draft' AND published_at IS NULL) OR
           (status = 'published' AND published_at IS NOT NULL))
);

CREATE TABLE harness.workflow_phase_definitions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    definition_version_id char(26) NOT NULL,
    phase_key varchar(100) NOT NULL,
    name varchar(200) NOT NULL,
    phase_order integer NOT NULL CHECK (phase_order > 0),
    UNIQUE (definition_version_id, phase_key),
    UNIQUE (definition_version_id, phase_order),
    UNIQUE (tenant_id, id),
    UNIQUE (id, definition_version_id),
    FOREIGN KEY (tenant_id, definition_version_id)
        REFERENCES harness.workflow_definition_versions(tenant_id, id),
    CHECK (length(trim(phase_key)) > 0),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.workflow_objective_definitions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    phase_definition_id char(26) NOT NULL,
    objective_key varchar(100) NOT NULL,
    name varchar(200) NOT NULL,
    kind varchar(20) NOT NULL
        CHECK (kind IN ('document', 'task', 'test', 'gate', 'approval', 'evidence')),
    weight numeric(18,6) NOT NULL CHECK (weight > 0),
    UNIQUE (phase_definition_id, objective_key),
    UNIQUE (tenant_id, id),
    UNIQUE (phase_definition_id, id),
    FOREIGN KEY (tenant_id, phase_definition_id)
        REFERENCES harness.workflow_phase_definitions(tenant_id, id),
    CHECK (length(trim(objective_key)) > 0),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.workflow_gate_definitions
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    phase_definition_id char(26) NOT NULL,
    objective_definition_id char(26) NOT NULL,
    gate_key varchar(100) NOT NULL,
    name varchar(200) NOT NULL,
    minimum_required_state varchar(20) NOT NULL
        CHECK (minimum_required_state IN ('executed', 'validated', 'approved')),
    UNIQUE (phase_definition_id, gate_key),
    UNIQUE (phase_definition_id, objective_definition_id),
    UNIQUE (tenant_id, id),
    UNIQUE (phase_definition_id, id),
    FOREIGN KEY (tenant_id, phase_definition_id)
        REFERENCES harness.workflow_phase_definitions(tenant_id, id),
    FOREIGN KEY (phase_definition_id, objective_definition_id)
        REFERENCES harness.workflow_objective_definitions(phase_definition_id, id),
    CHECK (length(trim(gate_key)) > 0),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.workflow_gate_requirements
(
    phase_definition_id char(26) NOT NULL,
    gate_definition_id char(26) NOT NULL,
    objective_definition_id char(26) NOT NULL,
    requirement_order integer NOT NULL CHECK (requirement_order > 0),
    PRIMARY KEY (gate_definition_id, objective_definition_id),
    UNIQUE (gate_definition_id, requirement_order),
    FOREIGN KEY (phase_definition_id, gate_definition_id)
        REFERENCES harness.workflow_gate_definitions(phase_definition_id, id),
    FOREIGN KEY (phase_definition_id, objective_definition_id)
        REFERENCES harness.workflow_objective_definitions(phase_definition_id, id),
    CHECK (gate_definition_id <> objective_definition_id)
);

CREATE TABLE harness.workflow_runs
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    definition_version_id char(26) NOT NULL,
    state varchar(20) NOT NULL
        CHECK (state IN ('pending', 'running', 'paused', 'completed', 'cancelled')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL,
    started_at timestamptz NULL,
    completed_at timestamptz NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, definition_version_id)
        REFERENCES harness.workflow_definition_versions(tenant_id, id),
    CHECK ((state = 'pending' AND started_at IS NULL AND completed_at IS NULL) OR
           (state IN ('running', 'paused') AND started_at IS NOT NULL AND completed_at IS NULL) OR
           (state IN ('completed', 'cancelled') AND completed_at IS NOT NULL))
);

CREATE TABLE harness.workflow_phase_runs
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    workflow_run_id char(26) NOT NULL,
    phase_definition_id char(26) NOT NULL,
    phase_order integer NOT NULL CHECK (phase_order > 0),
    state varchar(20) NOT NULL CHECK (state IN ('pending', 'active', 'completed')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    activated_at timestamptz NULL,
    completed_at timestamptz NULL,
    UNIQUE (workflow_run_id, phase_definition_id),
    UNIQUE (workflow_run_id, phase_order),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, workflow_run_id)
        REFERENCES harness.workflow_runs(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, phase_definition_id)
        REFERENCES harness.workflow_phase_definitions(tenant_id, id),
    CHECK ((state = 'pending' AND activated_at IS NULL AND completed_at IS NULL) OR
           (state = 'active' AND activated_at IS NOT NULL AND completed_at IS NULL) OR
           (state = 'completed' AND activated_at IS NOT NULL AND completed_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_workflow_phase_runs_one_active
    ON harness.workflow_phase_runs (workflow_run_id) WHERE state = 'active';

CREATE TABLE harness.workflow_objective_runs
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    phase_run_id char(26) NOT NULL,
    objective_definition_id char(26) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('pending', 'executed', 'validated', 'approved')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at timestamptz NOT NULL,
    UNIQUE (phase_run_id, objective_definition_id),
    FOREIGN KEY (tenant_id, project_id, phase_run_id)
        REFERENCES harness.workflow_phase_runs(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, objective_definition_id)
        REFERENCES harness.workflow_objective_definitions(tenant_id, id)
);

CREATE TABLE harness.workflow_gate_runs
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    phase_run_id char(26) NOT NULL,
    gate_definition_id char(26) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('pending', 'failed', 'passed')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    evaluated_at timestamptz NULL,
    UNIQUE (phase_run_id, gate_definition_id),
    FOREIGN KEY (tenant_id, project_id, phase_run_id)
        REFERENCES harness.workflow_phase_runs(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, gate_definition_id)
        REFERENCES harness.workflow_gate_definitions(tenant_id, id),
    CHECK ((state = 'pending' AND evaluated_at IS NULL) OR
           (state IN ('failed', 'passed') AND evaluated_at IS NOT NULL))
);

CREATE INDEX ix_workflow_versions_definition
    ON harness.workflow_definition_versions (definition_id, version);
CREATE INDEX ix_workflow_phases_version
    ON harness.workflow_phase_definitions (definition_version_id, phase_order);
CREATE INDEX ix_workflow_objectives_phase
    ON harness.workflow_objective_definitions (phase_definition_id, objective_key);
CREATE INDEX ix_workflow_runs_project_state
    ON harness.workflow_runs (tenant_id, project_id, state, created_at, id);
CREATE INDEX ix_workflow_phase_runs_run
    ON harness.workflow_phase_runs (workflow_run_id, phase_order);
