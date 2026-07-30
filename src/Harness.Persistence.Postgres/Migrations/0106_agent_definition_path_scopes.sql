-- Paridade com o SQLite: escopo de path da persona (B8/F17). Sem estes campos o lint de definição
-- não tem o que julgar — ele decide sobre CAMINHOS, e a definição só declarava tecnologia e
-- limitações em texto livre.
ALTER TABLE harness.agent_definitions ADD COLUMN allowed_scopes_json jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK(jsonb_typeof(allowed_scopes_json)='array');
ALTER TABLE harness.agent_definitions ADD COLUMN denied_scopes_json jsonb NOT NULL DEFAULT '[]'::jsonb
    CHECK(jsonb_typeof(denied_scopes_json)='array');
