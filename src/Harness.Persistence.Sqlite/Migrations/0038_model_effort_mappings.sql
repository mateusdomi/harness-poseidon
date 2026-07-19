ALTER TABLE provider_models ADD COLUMN effort_mappings_json TEXT NOT NULL DEFAULT '[]'
    CHECK(json_valid(effort_mappings_json) AND json_type(effort_mappings_json)='array');

UPDATE provider_models SET effort_mappings_json =
    '[{"effort":"low","providerValue":"low"},{"effort":"medium","providerValue":"medium"},{"effort":"high","providerValue":"high"},{"effort":"max","providerValue":"xhigh"}]'
WHERE provider_id='01ARZ3NDEKTSV4RRFFQ69G5FG1';
UPDATE provider_models SET effort_mappings_json =
    '[{"effort":"low","providerValue":"low"},{"effort":"medium","providerValue":"medium"},{"effort":"high","providerValue":"high"},{"effort":"max","providerValue":"high"}]'
WHERE provider_id IN('01ARZ3NDEKTSV4RRFFQ69G5FG2','01ARZ3NDEKTSV4RRFFQ69G5FG3');
