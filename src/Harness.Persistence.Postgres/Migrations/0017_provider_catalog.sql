CREATE TABLE harness.providers
(
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    id char(26) NOT NULL,
    kind varchar(50) NOT NULL CHECK (kind IN ('openai', 'anthropic', 'azureOpenai', 'google', 'ollama', 'custom')),
    name varchar(200) NOT NULL,
    base_url text NULL,
    enabled boolean NOT NULL,
    last_synced_at timestamptz NULL,
    PRIMARY KEY (tenant_id, id)
);

CREATE TABLE harness.provider_accounts
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    provider_id char(26) NOT NULL,
    label varchar(200) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('active', 'disabled', 'quotaExceeded')),
    credential_reference text NULL,
    quota_limit_usd numeric NULL CHECK (quota_limit_usd IS NULL OR quota_limit_usd >= 0),
    quota_used_usd numeric NOT NULL DEFAULT 0 CHECK (quota_used_usd >= 0),
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, provider_id) REFERENCES harness.providers(tenant_id, id)
);

CREATE TABLE harness.provider_models
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    provider_id char(26) NOT NULL,
    model_name varchar(200) NOT NULL,
    display_name varchar(200) NOT NULL,
    capabilities_json jsonb NOT NULL CHECK (jsonb_typeof(capabilities_json) = 'array'),
    context_window integer NOT NULL CHECK (context_window > 0),
    cost_input numeric NULL CHECK (cost_input IS NULL OR cost_input >= 0),
    cost_output numeric NULL CHECK (cost_output IS NULL OR cost_output >= 0),
    enabled boolean NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, provider_id, model_name),
    FOREIGN KEY (tenant_id, provider_id) REFERENCES harness.providers(tenant_id, id)
);

CREATE TABLE harness.routing_policies
(
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    id char(26) NOT NULL,
    project_id char(26) NULL,
    name varchar(200) NOT NULL,
    rules_json jsonb NOT NULL CHECK (jsonb_typeof(rules_json) = 'array'),
    active boolean NOT NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

CREATE TABLE harness.budgets
(
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    id char(26) NOT NULL,
    scope varchar(20) NOT NULL CHECK (scope IN ('global', 'project', 'account')),
    scope_id char(26) NULL,
    period varchar(20) NOT NULL CHECK (period IN ('daily', 'weekly', 'monthly')),
    limit_usd numeric NOT NULL CHECK (limit_usd >= 0),
    spent_usd numeric NOT NULL DEFAULT 0 CHECK (spent_usd >= 0),
    alert_threshold_pct numeric NOT NULL CHECK (alert_threshold_pct BETWEEN 0 AND 100),
    PRIMARY KEY (tenant_id, id),
    CHECK ((scope = 'global' AND scope_id IS NULL) OR (scope <> 'global' AND scope_id IS NOT NULL))
);

CREATE INDEX ix_provider_accounts_provider ON harness.provider_accounts (tenant_id, provider_id, id);
CREATE INDEX ix_provider_models_provider ON harness.provider_models (tenant_id, provider_id, id);
CREATE INDEX ix_budgets_scope ON harness.budgets (tenant_id, scope, scope_id, id);

UPDATE harness.agent_definitions SET default_model_id = '01ARZ3NDEKTSV4RRFFQ69G5FJ1'
WHERE agent_key IN ('chief-orchestrator', 'product-requirements-analyst', 'technical-writer');
UPDATE harness.agent_definitions SET default_model_id = '01ARZ3NDEKTSV4RRFFQ69G5FJ2'
WHERE agent_key IN ('software-architect', 'software-engineer', 'critic-qa');
