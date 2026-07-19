ALTER TABLE workflow_definitions ADD COLUMN archived_at TEXT NULL;
ALTER TABLE workflow_definition_versions ADD COLUMN archived_at TEXT NULL;
