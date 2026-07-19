ALTER TABLE harness.workflow_definitions ADD COLUMN archived_at timestamptz NULL;
ALTER TABLE harness.workflow_definition_versions ADD COLUMN archived_at timestamptz NULL;
