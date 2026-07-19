ALTER TABLE harness.workflow_definitions ADD COLUMN description varchar(10000) NOT NULL DEFAULT '';

CREATE TABLE harness.workflow_bindings
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    project_id char(26) NOT NULL,
    definition_id char(26) NOT NULL,
    active_version_id char(26) NOT NULL,
    operation_mode varchar(30) NOT NULL
        CHECK (operation_mode IN ('manual', 'semiautonomous', 'autonomous')),
    pause_gates_json jsonb NOT NULL DEFAULT '[]'
        CHECK (jsonb_typeof(pause_gates_json) = 'array'),
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, project_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, definition_id) REFERENCES harness.workflow_definitions(tenant_id, id),
    FOREIGN KEY (tenant_id, active_version_id)
        REFERENCES harness.workflow_definition_versions(tenant_id, id)
);

CREATE TABLE harness.workflow_risk_acceptances
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    workflow_id char(26) NOT NULL,
    mode varchar(30) NOT NULL CHECK (mode IN ('manual', 'semiautonomous', 'autonomous')),
    accepted_by_profile_id char(26) NOT NULL,
    note varchar(10000) NOT NULL CHECK (length(note) > 0),
    accepted_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, workflow_id) REFERENCES harness.workflow_bindings(tenant_id, id),
    FOREIGN KEY (tenant_id, accepted_by_profile_id) REFERENCES harness.local_users(tenant_id, id)
);

ALTER TABLE harness.workflow_runs ADD COLUMN workflow_id char(26) NULL
    REFERENCES harness.workflow_bindings(id);
ALTER TABLE harness.workflow_gate_runs ADD COLUMN decided_by_profile_id char(26) NULL;
ALTER TABLE harness.workflow_gate_runs ADD COLUMN decision_note varchar(10000) NULL;

CREATE INDEX ix_workflow_bindings_project
    ON harness.workflow_bindings (tenant_id, project_id, id);
CREATE INDEX ix_workflow_runs_binding
    ON harness.workflow_runs (tenant_id, workflow_id, created_at, id);
CREATE INDEX ix_workflow_acceptances_binding
    ON harness.workflow_risk_acceptances (tenant_id, workflow_id, accepted_at, id);

ALTER TABLE harness.workflow_definition_versions ADD COLUMN phase_configs_json jsonb NOT NULL
    DEFAULT '{}' CHECK (jsonb_typeof(phase_configs_json) = 'object');
ALTER TABLE harness.workflow_definition_versions ADD COLUMN default_operation_mode varchar(30) NULL
    CHECK (default_operation_mode IS NULL OR
           default_operation_mode IN ('manual', 'semiautonomous', 'autonomous'));
ALTER TABLE harness.workflow_definition_versions ADD COLUMN transitions_json jsonb NOT NULL
    DEFAULT '{}' CHECK (jsonb_typeof(transitions_json) = 'object');
ALTER TABLE harness.workflow_definition_versions ADD COLUMN changelog varchar(10000) NULL;
