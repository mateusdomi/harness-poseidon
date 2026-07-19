ALTER TABLE harness.organizations ADD COLUMN slug varchar(200) NOT NULL DEFAULT '';
ALTER TABLE harness.organizations ADD COLUMN plan varchar(50) NOT NULL DEFAULT 'personal';
ALTER TABLE harness.organizations ADD COLUMN logo_url text NULL;
ALTER TABLE harness.organizations ADD COLUMN primary_color varchar(50) NULL;
ALTER TABLE harness.organizations ADD COLUMN secondary_color varchar(50) NULL;
ALTER TABLE harness.organizations ADD COLUMN typography varchar(200) NULL;
ALTER TABLE harness.organizations ADD COLUMN default_workflow_template_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE harness.organizations ADD COLUMN template_keys_json jsonb NOT NULL DEFAULT '[]'::jsonb;
ALTER TABLE harness.organizations ADD COLUMN policies_json jsonb NOT NULL DEFAULT '[]'::jsonb;

CREATE UNIQUE INDEX ux_organizations_tenant_slug
    ON harness.organizations (tenant_id, lower(slug))
    WHERE slug <> '';
