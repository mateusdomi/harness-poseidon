ALTER TABLE workflow_definition_versions ADD COLUMN phase_configs_json TEXT NOT NULL DEFAULT '{}'
    CHECK (json_valid(phase_configs_json));
ALTER TABLE workflow_definition_versions ADD COLUMN default_operation_mode TEXT NULL
    CHECK (default_operation_mode IS NULL OR default_operation_mode IN ('manual','semiautonomous','autonomous'));
ALTER TABLE workflow_definition_versions ADD COLUMN transitions_json TEXT NOT NULL DEFAULT '{}'
    CHECK (json_valid(transitions_json));
ALTER TABLE workflow_definition_versions ADD COLUMN changelog TEXT NULL;
