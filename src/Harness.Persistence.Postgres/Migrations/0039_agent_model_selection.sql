ALTER TABLE harness.agents
    ADD COLUMN account_id char(26) NULL,
    ADD COLUMN effort varchar(20) NULL CHECK(effort IS NULL OR effort IN('low','medium','high','max')),
    ADD COLUMN provider_effort_value varchar(50) NULL,
    ADD COLUMN fallback_model_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb CHECK(jsonb_typeof(fallback_model_ids_json)='array'),
    ADD COLUMN selection_reason text NULL,
    ADD COLUMN selection_updated_at timestamptz NULL;

CREATE INDEX ix_agents_account ON harness.agents(tenant_id,account_id,id);
