ALTER TABLE agents ADD COLUMN account_id TEXT NULL;
ALTER TABLE agents ADD COLUMN effort TEXT NULL CHECK(effort IS NULL OR effort IN('low','medium','high','max'));
ALTER TABLE agents ADD COLUMN provider_effort_value TEXT NULL;
ALTER TABLE agents ADD COLUMN fallback_model_ids_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(fallback_model_ids_json) AND json_type(fallback_model_ids_json)='array');
ALTER TABLE agents ADD COLUMN selection_reason TEXT NULL;
ALTER TABLE agents ADD COLUMN selection_updated_at TEXT NULL;

CREATE INDEX ix_agents_account ON agents(tenant_id,account_id,id);
