ALTER TABLE chief_turn_mailbox ADD COLUMN account_id TEXT NULL;
ALTER TABLE chief_turn_mailbox ADD COLUMN model_id TEXT NULL;
ALTER TABLE chief_turn_mailbox ADD COLUMN model_name TEXT NULL;
ALTER TABLE chief_turn_mailbox ADD COLUMN effort TEXT NULL CHECK(effort IS NULL OR effort IN('low','medium','high','max'));
ALTER TABLE chief_turn_mailbox ADD COLUMN provider_effort_value TEXT NULL;
ALTER TABLE chief_turn_mailbox ADD COLUMN fallback_model_ids_json TEXT NOT NULL DEFAULT '[]'
    CHECK(json_valid(fallback_model_ids_json) AND json_type(fallback_model_ids_json)='array');
ALTER TABLE chief_turn_mailbox ADD COLUMN selection_source TEXT NULL
    CHECK(selection_source IS NULL OR selection_source IN('explicit','agent','definition'));
ALTER TABLE chief_turn_mailbox ADD COLUMN selection_reason TEXT NULL;
ALTER TABLE chief_turn_mailbox ADD COLUMN estimated_cost_usd NUMERIC NULL
    CHECK(estimated_cost_usd IS NULL OR estimated_cost_usd>=0);
ALTER TABLE chief_turn_mailbox ADD COLUMN quota_remaining_usd NUMERIC NULL
    CHECK(quota_remaining_usd IS NULL OR quota_remaining_usd>=0);

CREATE INDEX ix_chief_turn_selection_model ON chief_turn_mailbox(tenant_id,model_id,created_at,id);
