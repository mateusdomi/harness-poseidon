ALTER TABLE workflow_definitions ADD COLUMN description TEXT NOT NULL DEFAULT '';

CREATE TABLE workflow_bindings
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    project_id TEXT NOT NULL,
    definition_id TEXT NOT NULL,
    active_version_id TEXT NOT NULL,
    operation_mode TEXT NOT NULL CHECK (operation_mode IN ('manual','semiautonomous','autonomous')),
    pause_gates_json TEXT NOT NULL DEFAULT '[]' CHECK (json_valid(pause_gates_json)),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,project_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id),
    FOREIGN KEY (tenant_id,definition_id) REFERENCES workflow_definitions(tenant_id,id),
    FOREIGN KEY (tenant_id,active_version_id) REFERENCES workflow_definition_versions(tenant_id,id)
);

CREATE TABLE workflow_risk_acceptances
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    workflow_id TEXT NOT NULL,
    mode TEXT NOT NULL CHECK (mode IN ('manual','semiautonomous','autonomous')),
    accepted_by_profile_id TEXT NOT NULL,
    note TEXT NOT NULL CHECK (length(note) BETWEEN 1 AND 10000),
    accepted_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,workflow_id) REFERENCES workflow_bindings(tenant_id,id),
    FOREIGN KEY (tenant_id,accepted_by_profile_id) REFERENCES local_users(tenant_id,id)
);

ALTER TABLE workflow_runs ADD COLUMN workflow_id TEXT NULL REFERENCES workflow_bindings(id);
ALTER TABLE workflow_gate_runs ADD COLUMN decided_by_profile_id TEXT NULL;
ALTER TABLE workflow_gate_runs ADD COLUMN decision_note TEXT NULL;

CREATE INDEX ix_workflow_bindings_project ON workflow_bindings (tenant_id,project_id,id);
CREATE INDEX ix_workflow_runs_binding ON workflow_runs (tenant_id,workflow_id,created_at,id);
CREATE INDEX ix_workflow_acceptances_binding ON workflow_risk_acceptances (tenant_id,workflow_id,accepted_at,id);
