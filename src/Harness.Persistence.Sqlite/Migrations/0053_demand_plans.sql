-- PLAT-01: artefato de PLANO durável por demanda. O planner PURO decompõe a demanda em cards
-- filhos ATÔMICOS (backend/frontend/integração/human-gate/spike/decision) seguindo a política de
-- criação de cards; o plano gerado é persistido aqui com status 'proposed' e sobrevive a reinício.
-- INÉRCIA por design: gerar/ler um plano NÃO cria nada na esteira; ele só vira work_tasks quando
-- alguém o MATERIALIZA. A materialização carimba status='materialized' + materialized_at, tornando
-- a operação idempotente por plano (não recria os cards em uma segunda chamada).
--
-- IDEMPOTÊNCIA da geração: a unicidade por (tenant, demand) garante um plano ativo por demanda —
-- regenerar devolve o plano existente (INSERT OR IGNORE no store), preservando a auditoria.
CREATE TABLE demand_plans
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    demand_id TEXT NOT NULL,
    feature_id TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'proposed' CHECK (status IN ('proposed','materialized')),
    cards_json TEXT NOT NULL CHECK (json_valid(cards_json) AND json_type(cards_json) = 'array'),
    correlation_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    materialized_at TEXT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,demand_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id),
    FOREIGN KEY (tenant_id,project_id,demand_id) REFERENCES demands(tenant_id,project_id,id)
);

CREATE INDEX ix_demand_plans_scope
    ON demand_plans (tenant_id,project_id,created_at,id);
