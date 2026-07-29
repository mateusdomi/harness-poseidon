-- Paridade com o SQLite: as guardas de laço da Bruna precisam de estado durável — em memória o
-- laço reinicia junto com o processo.
CREATE TABLE harness.chief_self_triggered_turns
(
    tenant_id text NOT NULL,
    id text NOT NULL CHECK (length(id) = 26),
    project_id text NOT NULL,
    demand_id text NOT NULL,
    demand_plan_id text NULL,
    chief_turn_id text NULL,
    cause_key text NULL CHECK (cause_key IS NULL OR length(cause_key) <= 200),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id)
);

CREATE INDEX ix_chief_self_triggered_turns_demand
    ON harness.chief_self_triggered_turns (tenant_id, demand_id, occurred_at);

CREATE TABLE harness.chief_causal_edges
(
    tenant_id text NOT NULL,
    id text NOT NULL CHECK (length(id) = 26),
    project_id text NOT NULL,
    cause_key text NOT NULL CHECK (length(cause_key) BETWEEN 1 AND 200),
    effect_key text NOT NULL CHECK (length(effect_key) BETWEEN 1 AND 200),
    relation text NOT NULL CHECK (length(relation) BETWEEN 1 AND 100),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, cause_key, effect_key, relation)
);

CREATE INDEX ix_chief_causal_edges_cause
    ON harness.chief_causal_edges (tenant_id, project_id, cause_key);

CREATE TABLE harness.chief_loop_interruptions
(
    tenant_id text NOT NULL,
    id text NOT NULL CHECK (length(id) = 26),
    project_id text NOT NULL,
    demand_id text NOT NULL,
    demand_plan_id text NULL,
    reason_code text NOT NULL CHECK (reason_code IN (
        'chief_loop.demand_ceiling',
        'chief_loop.plan_reentrancy',
        'chief_loop.rate_window',
        'chief_loop.causal_cycle')),
    detail text NULL CHECK (detail IS NULL OR length(detail) <= 4000),
    cycle_path_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id)
);

CREATE INDEX ix_chief_loop_interruptions_demand
    ON harness.chief_loop_interruptions (tenant_id, demand_id, occurred_at);

ALTER TABLE harness.chief_self_triggered_turns ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_self_triggered_turns FORCE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_causal_edges ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_causal_edges FORCE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_loop_interruptions ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_loop_interruptions FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.chief_self_triggered_turns
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));

CREATE POLICY tenant_isolation ON harness.chief_causal_edges
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));

CREATE POLICY tenant_isolation ON harness.chief_loop_interruptions
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
