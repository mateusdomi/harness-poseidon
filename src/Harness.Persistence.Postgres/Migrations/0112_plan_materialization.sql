-- Paridade com o SQLite: materialização durável e idempotente de plano → cards (Fase 0A1,
-- BR-001/BR-004). Mesma semântica, mesmos nomes lógicos, mesma chave de idempotência.
--
-- `demand_materializations` é o compromisso durável gravado na MESMA transação que conclui o turno
-- do Chefe; `work_tasks.plan_id`/`plan_slice_key` são a chave lógica do card dentro do plano, e o
-- índice único parcial impede duplicata em retry ou execução concorrente.

ALTER TABLE harness.work_tasks ADD COLUMN plan_id text NULL;
ALTER TABLE harness.work_tasks ADD COLUMN plan_slice_key text NULL;

CREATE UNIQUE INDEX ux_work_tasks_plan_slice
    ON harness.work_tasks (tenant_id, plan_id, plan_slice_key)
    WHERE plan_id IS NOT NULL AND plan_slice_key IS NOT NULL;

CREATE TABLE harness.demand_materializations
(
    tenant_id text NOT NULL,
    demand_id char(26) NOT NULL,
    project_id text NOT NULL,
    turn_id text NULL,
    plan_id text NULL,
    status text NOT NULL
        CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    expected_cards integer NULL CHECK (expected_cards IS NULL OR expected_cards >= 0),
    materialized_cards integer NULL CHECK (materialized_cards IS NULL OR materialized_cards >= 0),
    request_json jsonb NOT NULL,
    owner_id text NULL,
    last_error text NULL CHECK (last_error IS NULL OR length(last_error) <= 4000),
    requested_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    completed_at timestamptz NULL,
    PRIMARY KEY (tenant_id, demand_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects (tenant_id, id),
    -- O compromisso nasce na mesma transação que insere a demanda; um registro apontando para uma
    -- demanda inexistente seria trabalho prometido a ninguém.
    FOREIGN KEY (tenant_id, project_id, demand_id)
        REFERENCES harness.demands (tenant_id, project_id, id)
);

CREATE INDEX ix_demand_materializations_open
    ON harness.demand_materializations (updated_at, tenant_id, demand_id)
    WHERE status <> 'completed';

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.demand_materializations ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.demand_materializations FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.demand_materializations
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
