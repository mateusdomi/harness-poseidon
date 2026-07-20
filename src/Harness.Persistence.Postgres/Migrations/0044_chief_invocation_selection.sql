ALTER TABLE harness.chief_turn_mailbox
    ADD COLUMN account_id char(26) NULL,
    ADD COLUMN model_id char(26) NULL,
    ADD COLUMN model_name varchar(200) NULL,
    ADD COLUMN effort varchar(20) NULL CHECK(effort IS NULL OR effort IN('low','medium','high','max')),
    ADD COLUMN provider_effort_value varchar(100) NULL,
    ADD COLUMN fallback_model_ids_json jsonb NOT NULL DEFAULT '[]'::jsonb
        CHECK(jsonb_typeof(fallback_model_ids_json)='array'),
    ADD COLUMN selection_source varchar(20) NULL
        CHECK(selection_source IS NULL OR selection_source IN('explicit','agent','definition')),
    ADD COLUMN selection_reason varchar(1000) NULL,
    ADD COLUMN estimated_cost_usd numeric NULL CHECK(estimated_cost_usd IS NULL OR estimated_cost_usd>=0),
    ADD COLUMN quota_remaining_usd numeric NULL CHECK(quota_remaining_usd IS NULL OR quota_remaining_usd>=0);

CREATE INDEX ix_chief_turn_selection_model
    ON harness.chief_turn_mailbox(tenant_id,model_id,created_at,id);
