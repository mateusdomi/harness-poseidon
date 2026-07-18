ALTER TABLE projects ADD COLUMN project_key TEXT NOT NULL DEFAULT '';
ALTER TABLE projects ADD COLUMN description TEXT NOT NULL DEFAULT '';
ALTER TABLE projects ADD COLUMN state TEXT NOT NULL DEFAULT 'active';
ALTER TABLE projects ADD COLUMN criticality TEXT NOT NULL DEFAULT 'medium';
ALTER TABLE projects ADD COLUMN repository_url TEXT NULL;
ALTER TABLE projects ADD COLUMN repository_provider TEXT NOT NULL DEFAULT 'local';
ALTER TABLE projects ADD COLUMN default_branch TEXT NOT NULL DEFAULT 'main';
ALTER TABLE projects ADD COLUMN technologies_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE projects ADD COLUMN logo_url TEXT NULL;
ALTER TABLE projects ADD COLUMN primary_color TEXT NULL;
ALTER TABLE projects ADD COLUMN secondary_color TEXT NULL;
ALTER TABLE projects ADD COLUMN typography TEXT NULL;
ALTER TABLE projects ADD COLUMN member_profile_ids_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE projects ADD COLUMN config_version INTEGER NOT NULL DEFAULT 1;
ALTER TABLE projects ADD COLUMN chief_agent_id TEXT NOT NULL DEFAULT '';
ALTER TABLE projects ADD COLUMN operation_mode TEXT NOT NULL DEFAULT 'manual';
ALTER TABLE projects ADD COLUMN last_activity_at TEXT NULL;
ALTER TABLE projects ADD COLUMN deleted_at TEXT NULL;

UPDATE projects SET last_activity_at=created_at WHERE last_activity_at IS NULL;

CREATE UNIQUE INDEX ux_projects_organization_key
    ON projects (organization_id, upper(project_key)) WHERE project_key <> '' AND deleted_at IS NULL;
CREATE INDEX ix_projects_tenant_active ON projects (tenant_id,id) WHERE deleted_at IS NULL;
