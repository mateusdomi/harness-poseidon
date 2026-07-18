ALTER TABLE organizations ADD COLUMN slug TEXT NOT NULL DEFAULT '';
ALTER TABLE organizations ADD COLUMN plan TEXT NOT NULL DEFAULT 'personal';
ALTER TABLE organizations ADD COLUMN logo_url TEXT NULL;
ALTER TABLE organizations ADD COLUMN primary_color TEXT NULL;
ALTER TABLE organizations ADD COLUMN secondary_color TEXT NULL;
ALTER TABLE organizations ADD COLUMN typography TEXT NULL;
ALTER TABLE organizations ADD COLUMN default_workflow_template_ids_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE organizations ADD COLUMN template_keys_json TEXT NOT NULL DEFAULT '[]';
ALTER TABLE organizations ADD COLUMN policies_json TEXT NOT NULL DEFAULT '[]';

CREATE UNIQUE INDEX ux_organizations_tenant_slug
    ON organizations (tenant_id, lower(slug))
    WHERE slug <> '';
