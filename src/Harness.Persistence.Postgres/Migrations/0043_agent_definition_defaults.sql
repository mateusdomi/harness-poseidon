ALTER TABLE harness.agent_definitions ADD COLUMN stacks_json jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK(jsonb_typeof(stacks_json)='array');
ALTER TABLE harness.agent_definitions ADD COLUMN default_effort varchar(20) NULL
    CHECK(default_effort IS NULL OR default_effort IN ('low','medium','high','max'));
ALTER TABLE harness.agent_definitions ADD COLUMN preferred_account_id char(26) NULL;
ALTER TABLE harness.agent_definitions ADD COLUMN fallback_model_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK(jsonb_typeof(fallback_model_ids_json)='array');
ALTER TABLE harness.agent_definitions ADD COLUMN team varchar(200) NULL;
ALTER TABLE harness.agent_definitions ADD COLUMN actor_critic varchar(20) NULL
    CHECK(actor_critic IS NULL OR actor_critic IN ('actor','critic'));
ALTER TABLE harness.agent_definitions ADD COLUMN risk varchar(20) NULL
    CHECK(risk IS NULL OR risk IN ('low','medium','high'));

CREATE INDEX ix_agent_definitions_team ON harness.agent_definitions(tenant_id,team,id);
CREATE INDEX ix_agent_definitions_preferred_account ON harness.agent_definitions(tenant_id,preferred_account_id,id);
