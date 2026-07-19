ALTER TABLE harness.projects ADD COLUMN project_key varchar(50) NOT NULL DEFAULT '';
ALTER TABLE harness.projects ADD COLUMN description text NOT NULL DEFAULT '';
ALTER TABLE harness.projects ADD COLUMN state varchar(50) NOT NULL DEFAULT 'active';
ALTER TABLE harness.projects ADD COLUMN criticality varchar(50) NOT NULL DEFAULT 'medium';
ALTER TABLE harness.projects ADD COLUMN repository_url text NULL;
ALTER TABLE harness.projects ADD COLUMN repository_provider varchar(50) NOT NULL DEFAULT 'local';
ALTER TABLE harness.projects ADD COLUMN default_branch varchar(200) NOT NULL DEFAULT 'main';
ALTER TABLE harness.projects ADD COLUMN technologies_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE harness.projects ADD COLUMN logo_url text NULL;
ALTER TABLE harness.projects ADD COLUMN primary_color varchar(50) NULL;
ALTER TABLE harness.projects ADD COLUMN secondary_color varchar(50) NULL;
ALTER TABLE harness.projects ADD COLUMN typography varchar(200) NULL;
ALTER TABLE harness.projects ADD COLUMN member_profile_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE harness.projects ADD COLUMN config_version bigint NOT NULL DEFAULT 1;
ALTER TABLE harness.projects ADD COLUMN chief_agent_id char(26) NOT NULL DEFAULT '';
ALTER TABLE harness.projects ADD COLUMN operation_mode varchar(50) NOT NULL DEFAULT 'manual';
ALTER TABLE harness.projects ADD COLUMN last_activity_at timestamptz NULL;
ALTER TABLE harness.projects ADD COLUMN deleted_at timestamptz NULL;

UPDATE harness.projects SET last_activity_at = created_at WHERE last_activity_at IS NULL;

CREATE UNIQUE INDEX ux_projects_organization_key
    ON harness.projects (organization_id, upper(project_key))
    WHERE project_key <> '' AND deleted_at IS NULL;
CREATE INDEX ix_projects_tenant_active ON harness.projects (tenant_id, id) WHERE deleted_at IS NULL;
