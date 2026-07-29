CREATE INDEX IF NOT EXISTS ix_projects_target_deadline ON harness.projects (tenant_id, target_deadline) WHERE target_deadline IS NOT NULL AND deleted_at IS NULL;
