ALTER TABLE agent_definitions ADD COLUMN stacks_json TEXT NOT NULL DEFAULT '[]'
    CHECK(json_valid(stacks_json) AND json_type(stacks_json)='array');
ALTER TABLE agent_definitions ADD COLUMN default_effort TEXT NULL
    CHECK(default_effort IS NULL OR default_effort IN ('low','medium','high','max'));
ALTER TABLE agent_definitions ADD COLUMN preferred_account_id TEXT NULL;
ALTER TABLE agent_definitions ADD COLUMN fallback_model_ids_json TEXT NOT NULL DEFAULT '[]'
    CHECK(json_valid(fallback_model_ids_json) AND json_type(fallback_model_ids_json)='array');
ALTER TABLE agent_definitions ADD COLUMN team TEXT NULL CHECK(team IS NULL OR length(team) BETWEEN 1 AND 200);
ALTER TABLE agent_definitions ADD COLUMN actor_critic TEXT NULL
    CHECK(actor_critic IS NULL OR actor_critic IN ('actor','critic'));
ALTER TABLE agent_definitions ADD COLUMN risk TEXT NULL
    CHECK(risk IS NULL OR risk IN ('low','medium','high'));

CREATE INDEX ix_agent_definitions_team ON agent_definitions(tenant_id,team,id);
CREATE INDEX ix_agent_definitions_preferred_account ON agent_definitions(tenant_id,preferred_account_id,id);
