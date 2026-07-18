CREATE TABLE providers
(
    tenant_id TEXT NOT NULL REFERENCES tenants(id), id TEXT NOT NULL CHECK(length(id)=26),
    kind TEXT NOT NULL CHECK(kind IN ('openai','anthropic','azureOpenai','google','ollama','custom')),
    name TEXT NOT NULL, base_url TEXT NULL, enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
    last_synced_at TEXT NULL, PRIMARY KEY(tenant_id,id)
);
CREATE TABLE provider_accounts
(
    tenant_id TEXT NOT NULL, id TEXT NOT NULL CHECK(length(id)=26), provider_id TEXT NOT NULL,
    label TEXT NOT NULL, state TEXT NOT NULL CHECK(state IN('active','disabled','quotaExceeded')),
    credential_reference TEXT NULL, quota_limit_usd NUMERIC NULL CHECK(quota_limit_usd IS NULL OR quota_limit_usd>=0),
    quota_used_usd NUMERIC NOT NULL DEFAULT 0 CHECK(quota_used_usd>=0), PRIMARY KEY(tenant_id,id),
    FOREIGN KEY(tenant_id,provider_id) REFERENCES providers(tenant_id,id)
);
CREATE TABLE provider_models
(
    tenant_id TEXT NOT NULL, id TEXT NOT NULL CHECK(length(id)=26), provider_id TEXT NOT NULL,
    model_name TEXT NOT NULL, display_name TEXT NOT NULL, capabilities_json TEXT NOT NULL
      CHECK(json_valid(capabilities_json) AND json_type(capabilities_json)='array'),
    context_window INTEGER NOT NULL CHECK(context_window>0), cost_input NUMERIC NULL CHECK(cost_input IS NULL OR cost_input>=0),
    cost_output NUMERIC NULL CHECK(cost_output IS NULL OR cost_output>=0), enabled INTEGER NOT NULL CHECK(enabled IN(0,1)),
    PRIMARY KEY(tenant_id,id), UNIQUE(tenant_id,provider_id,model_name),
    FOREIGN KEY(tenant_id,provider_id) REFERENCES providers(tenant_id,id)
);
CREATE TABLE routing_policies
(
    tenant_id TEXT NOT NULL REFERENCES tenants(id), id TEXT NOT NULL CHECK(length(id)=26), project_id TEXT NULL,
    name TEXT NOT NULL, rules_json TEXT NOT NULL CHECK(json_valid(rules_json) AND json_type(rules_json)='array'),
    active INTEGER NOT NULL CHECK(active IN(0,1)), PRIMARY KEY(tenant_id,id),
    FOREIGN KEY(tenant_id,project_id) REFERENCES projects(tenant_id,id)
);
CREATE TABLE budgets
(
    tenant_id TEXT NOT NULL REFERENCES tenants(id), id TEXT NOT NULL CHECK(length(id)=26),
    scope TEXT NOT NULL CHECK(scope IN('global','project','account')), scope_id TEXT NULL,
    period TEXT NOT NULL CHECK(period IN('daily','weekly','monthly')), limit_usd NUMERIC NOT NULL CHECK(limit_usd>=0),
    spent_usd NUMERIC NOT NULL DEFAULT 0 CHECK(spent_usd>=0), alert_threshold_pct NUMERIC NOT NULL CHECK(alert_threshold_pct BETWEEN 0 AND 100),
    PRIMARY KEY(tenant_id,id), CHECK((scope='global' AND scope_id IS NULL) OR (scope<>'global' AND scope_id IS NOT NULL))
);
CREATE INDEX ix_provider_accounts_provider ON provider_accounts(tenant_id,provider_id,id);
CREATE INDEX ix_provider_models_provider ON provider_models(tenant_id,provider_id,id);
CREATE INDEX ix_budgets_scope ON budgets(tenant_id,scope,scope_id,id);

UPDATE agent_definitions SET default_model_id='01ARZ3NDEKTSV4RRFFQ69G5FJ1'
WHERE agent_key IN ('chief-orchestrator','product-requirements-analyst','technical-writer');
UPDATE agent_definitions SET default_model_id='01ARZ3NDEKTSV4RRFFQ69G5FJ2'
WHERE agent_key IN ('software-architect','software-engineer','critic-qa');
