CREATE TABLE workflow_definitions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id) REFERENCES tenants(id)
);

CREATE TABLE workflow_definition_versions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    definition_id TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version > 0),
    status TEXT NOT NULL CHECK (status IN ('draft', 'published')),
    content_hash TEXT NOT NULL CHECK (length(content_hash) = 64),
    created_at TEXT NOT NULL,
    published_at TEXT NULL,
    UNIQUE (definition_id, version),
    UNIQUE (tenant_id, id),
    UNIQUE (id, definition_id),
    FOREIGN KEY (tenant_id, definition_id) REFERENCES workflow_definitions(tenant_id, id),
    CHECK ((status = 'draft' AND published_at IS NULL) OR
           (status = 'published' AND published_at IS NOT NULL))
);

CREATE TABLE workflow_phase_definitions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    definition_version_id TEXT NOT NULL,
    phase_key TEXT NOT NULL CHECK (length(phase_key) BETWEEN 1 AND 100),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    phase_order INTEGER NOT NULL CHECK (phase_order > 0),
    UNIQUE (definition_version_id, phase_key),
    UNIQUE (definition_version_id, phase_order),
    UNIQUE (tenant_id, id),
    UNIQUE (id, definition_version_id),
    FOREIGN KEY (tenant_id, definition_version_id)
        REFERENCES workflow_definition_versions(tenant_id, id)
);

CREATE TABLE workflow_objective_definitions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    phase_definition_id TEXT NOT NULL,
    objective_key TEXT NOT NULL CHECK (length(objective_key) BETWEEN 1 AND 100),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    kind TEXT NOT NULL CHECK (kind IN ('document', 'task', 'test', 'gate', 'approval', 'evidence')),
    weight NUMERIC NOT NULL CHECK (weight > 0),
    UNIQUE (phase_definition_id, objective_key),
    UNIQUE (tenant_id, id),
    UNIQUE (phase_definition_id, id),
    FOREIGN KEY (tenant_id, phase_definition_id)
        REFERENCES workflow_phase_definitions(tenant_id, id)
);

CREATE TABLE workflow_gate_definitions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    phase_definition_id TEXT NOT NULL,
    objective_definition_id TEXT NOT NULL,
    gate_key TEXT NOT NULL CHECK (length(gate_key) BETWEEN 1 AND 100),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    minimum_required_state TEXT NOT NULL
        CHECK (minimum_required_state IN ('executed', 'validated', 'approved')),
    UNIQUE (phase_definition_id, gate_key),
    UNIQUE (phase_definition_id, objective_definition_id),
    UNIQUE (tenant_id, id),
    UNIQUE (phase_definition_id, id),
    FOREIGN KEY (tenant_id, phase_definition_id)
        REFERENCES workflow_phase_definitions(tenant_id, id),
    FOREIGN KEY (phase_definition_id, objective_definition_id)
        REFERENCES workflow_objective_definitions(phase_definition_id, id)
);

CREATE TABLE workflow_gate_requirements
(
    phase_definition_id TEXT NOT NULL,
    gate_definition_id TEXT NOT NULL,
    objective_definition_id TEXT NOT NULL,
    requirement_order INTEGER NOT NULL CHECK (requirement_order > 0),
    PRIMARY KEY (gate_definition_id, objective_definition_id),
    UNIQUE (gate_definition_id, requirement_order),
    FOREIGN KEY (phase_definition_id, gate_definition_id)
        REFERENCES workflow_gate_definitions(phase_definition_id, id),
    FOREIGN KEY (phase_definition_id, objective_definition_id)
        REFERENCES workflow_objective_definitions(phase_definition_id, id),
    CHECK (gate_definition_id <> objective_definition_id)
);

CREATE TABLE workflow_runs
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    definition_version_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending', 'running', 'paused', 'completed', 'cancelled')),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at TEXT NOT NULL,
    started_at TEXT NULL,
    completed_at TEXT NULL,
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    FOREIGN KEY (tenant_id, definition_version_id)
        REFERENCES workflow_definition_versions(tenant_id, id),
    CHECK ((state = 'pending' AND started_at IS NULL AND completed_at IS NULL) OR
           (state IN ('running', 'paused') AND started_at IS NOT NULL AND completed_at IS NULL) OR
           (state IN ('completed', 'cancelled') AND completed_at IS NOT NULL))
);

CREATE TABLE workflow_phase_runs
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    workflow_run_id TEXT NOT NULL,
    phase_definition_id TEXT NOT NULL,
    phase_order INTEGER NOT NULL CHECK (phase_order > 0),
    state TEXT NOT NULL CHECK (state IN ('pending', 'active', 'completed')),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    activated_at TEXT NULL,
    completed_at TEXT NULL,
    UNIQUE (workflow_run_id, phase_definition_id),
    UNIQUE (workflow_run_id, phase_order),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, workflow_run_id)
        REFERENCES workflow_runs(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, phase_definition_id)
        REFERENCES workflow_phase_definitions(tenant_id, id),
    CHECK ((state = 'pending' AND activated_at IS NULL AND completed_at IS NULL) OR
           (state = 'active' AND activated_at IS NOT NULL AND completed_at IS NULL) OR
           (state = 'completed' AND activated_at IS NOT NULL AND completed_at IS NOT NULL))
);

CREATE UNIQUE INDEX ux_workflow_phase_runs_one_active
    ON workflow_phase_runs (workflow_run_id) WHERE state = 'active';

CREATE TABLE workflow_objective_runs
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    phase_run_id TEXT NOT NULL,
    objective_definition_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending', 'executed', 'validated', 'approved')),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at TEXT NOT NULL,
    UNIQUE (phase_run_id, objective_definition_id),
    FOREIGN KEY (tenant_id, project_id, phase_run_id)
        REFERENCES workflow_phase_runs(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, objective_definition_id)
        REFERENCES workflow_objective_definitions(tenant_id, id)
);

CREATE TABLE workflow_gate_runs
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    phase_run_id TEXT NOT NULL,
    gate_definition_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending', 'failed', 'passed')),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    evaluated_at TEXT NULL,
    UNIQUE (phase_run_id, gate_definition_id),
    FOREIGN KEY (tenant_id, project_id, phase_run_id)
        REFERENCES workflow_phase_runs(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, gate_definition_id)
        REFERENCES workflow_gate_definitions(tenant_id, id),
    CHECK ((state = 'pending' AND evaluated_at IS NULL) OR
           (state IN ('failed', 'passed') AND evaluated_at IS NOT NULL))
);

CREATE INDEX ix_workflow_versions_definition ON workflow_definition_versions (definition_id, version);
CREATE INDEX ix_workflow_phases_version ON workflow_phase_definitions (definition_version_id, phase_order);
CREATE INDEX ix_workflow_objectives_phase ON workflow_objective_definitions (phase_definition_id, objective_key);
CREATE INDEX ix_workflow_runs_project_state ON workflow_runs (tenant_id, project_id, state, created_at, id);
CREATE INDEX ix_workflow_phase_runs_run ON workflow_phase_runs (workflow_run_id, phase_order);
