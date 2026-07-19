ALTER TABLE harness.agent_definitions
    ADD COLUMN tenant_id char(26) NULL,
    ADD COLUMN persona text NULL,
    ADD COLUMN mission text NULL,
    ADD COLUMN operating_principles_json jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(operating_principles_json)='array'),
    ADD COLUMN deliverables_json jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(deliverables_json)='array'),
    ADD COLUMN quality_criteria_json jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(quality_criteria_json)='array'),
    ADD COLUMN communication_style text NULL,
    ADD COLUMN limitations_json jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(limitations_json)='array'),
    ADD COLUMN version integer NOT NULL DEFAULT 1 CHECK(version>0),
    ADD COLUMN enabled boolean NOT NULL DEFAULT true,
    ADD COLUMN archived_at timestamptz NULL,
    ADD COLUMN created_at timestamptz NULL,
    ADD COLUMN updated_at timestamptz NULL;

CREATE TABLE harness.agent_definition_versions
(
    id char(26) PRIMARY KEY, definition_id char(26) NOT NULL REFERENCES harness.agent_definitions(id),
    version integer NOT NULL CHECK(version>0), snapshot_json jsonb NOT NULL,
    actor_profile_id char(26) NOT NULL, created_at timestamptz NOT NULL,
    UNIQUE(definition_id,version)
);
CREATE INDEX ix_agent_definitions_tenant ON harness.agent_definitions(tenant_id,archived_at,id);
