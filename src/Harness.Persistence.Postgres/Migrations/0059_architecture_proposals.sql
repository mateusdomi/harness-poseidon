-- ARC-05: uma PROPOSTA agrupa mudanças propostas por um agente sobre a arquitetura vigente. Enquanto
-- 'open', vive separada do modelo vigente. Aplicar carimba 'applied' + applied_at e exige
-- justificativa em mudança crítica; itens que tocam elemento travado NUNCA são sobrescritos.
CREATE TABLE harness.architecture_proposals
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    title text NOT NULL,
    status text NOT NULL CHECK (status IN ('open','applied','discarded')),
    justification text NULL,
    created_at timestamptz NOT NULL,
    applied_at timestamptz NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_proposals_scope
    ON harness.architecture_proposals (tenant_id,project_id,created_at,id);
