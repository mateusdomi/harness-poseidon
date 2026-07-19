ALTER TABLE agent_definitions ADD COLUMN tenant_id TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN persona TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN mission TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN operating_principles_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(operating_principles_json) AND json_type(operating_principles_json)='array');
ALTER TABLE agent_definitions ADD COLUMN deliverables_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(deliverables_json) AND json_type(deliverables_json)='array');
ALTER TABLE agent_definitions ADD COLUMN quality_criteria_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(quality_criteria_json) AND json_type(quality_criteria_json)='array');
ALTER TABLE agent_definitions ADD COLUMN communication_style TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN limitations_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(limitations_json) AND json_type(limitations_json)='array');
ALTER TABLE agent_definitions ADD COLUMN version INTEGER NOT NULL DEFAULT 1 CHECK(version>0);
ALTER TABLE agent_definitions ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1 CHECK(enabled IN(0,1));
ALTER TABLE agent_definitions ADD COLUMN archived_at TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN created_at TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN updated_at TEXT NULL;

CREATE TABLE agent_definition_versions
(
    id TEXT PRIMARY KEY CHECK(length(id)=26), definition_id TEXT NOT NULL REFERENCES agent_definitions(id),
    version INTEGER NOT NULL CHECK(version>0), snapshot_json TEXT NOT NULL CHECK(json_valid(snapshot_json)),
    actor_profile_id TEXT NOT NULL, created_at TEXT NOT NULL, UNIQUE(definition_id,version)
);
CREATE INDEX ix_agent_definitions_tenant ON agent_definitions(tenant_id,archived_at,id);
