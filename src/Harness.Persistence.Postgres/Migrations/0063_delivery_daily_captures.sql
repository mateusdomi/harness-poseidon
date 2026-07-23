-- DEL-03: Daily Copilot. Marcações tipadas capturadas DURANTE a daily (acesso, dependência, decisão,
-- prazo, escopo, documentação, risco). É um registro APPEND-ONLY (nunca update nem delete) e NÃO cria
-- nem atualiza cards de PO — apenas persiste a nota durável para compor o resumo pós-daily e a base do
-- próximo briefing. Deriva a identidade da entrega do projeto; não duplica dados de outras áreas.
CREATE TABLE harness.delivery_daily_captures
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    kind text NOT NULL CHECK (kind IN
        ('access','dependency','decision','deadline','scope','doc','risk')),
    note text NOT NULL,
    captured_by text NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES harness.projects(tenant_id,id)
);

CREATE INDEX ix_delivery_daily_captures_scope
    ON harness.delivery_daily_captures (tenant_id,project_id,created_at,id);
