ALTER TABLE harness.provider_models ADD COLUMN effort_mappings_json jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK(jsonb_typeof(effort_mappings_json) = 'array');

UPDATE harness.provider_models SET effort_mappings_json =
    '[{"effort":"low","providerValue":"low"},{"effort":"medium","providerValue":"medium"},{"effort":"high","providerValue":"high"},{"effort":"max","providerValue":"xhigh"}]'::jsonb
WHERE provider_id='01ARZ3NDEKTSV4RRFFQ69G5FG1';
UPDATE harness.provider_models SET effort_mappings_json =
    '[{"effort":"low","providerValue":"low"},{"effort":"medium","providerValue":"medium"},{"effort":"high","providerValue":"high"},{"effort":"max","providerValue":"high"}]'::jsonb
WHERE provider_id IN('01ARZ3NDEKTSV4RRFFQ69G5FG2','01ARZ3NDEKTSV4RRFFQ69G5FG3');
