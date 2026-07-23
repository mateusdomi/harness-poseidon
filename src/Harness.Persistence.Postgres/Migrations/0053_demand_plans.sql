-- PLAT-01: artefato de PLANO durável por demanda. O planner PURO decompõe a demanda em cards
-- filhos ATÔMICOS (backend/frontend/integração/human-gate/spike/decision) seguindo a política de
-- criação de cards; o plano gerado é persistido aqui com status 'proposed' e sobrevive a reinício.
-- INÉRCIA por design: gerar/ler um plano NÃO cria nada na esteira; ele só vira work_tasks quando
-- alguém o MATERIALIZA. A materialização carimba status='materialized' + materialized_at, tornando
-- a operação idempotente por plano (não recria os cards em uma segunda chamada).
--
-- IDEMPOTÊNCIA da geração: a unicidade por (tenant, demand) garante um plano ativo por demanda —
-- regenerar devolve o plano existente (ON CONFLICT DO NOTHING no store), preservando a auditoria.
CREATE TABLE harness.demand_plans
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    demand_id char(26) NOT NULL,
    feature_id text NOT NULL,
    status text NOT NULL DEFAULT 'proposed' CHECK (status IN ('proposed','materialized')),
    cards_json jsonb NOT NULL,
    correlation_id text NOT NULL,
    created_at timestamptz NOT NULL,
    materialized_at timestamptz NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,demand_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES harness.projects(tenant_id,id),
    FOREIGN KEY (tenant_id,project_id,demand_id) REFERENCES harness.demands(tenant_id,project_id,id),
    CHECK (jsonb_typeof(cards_json) = 'array')
);

CREATE INDEX ix_demand_plans_scope
    ON harness.demand_plans (tenant_id,project_id,created_at,id);
