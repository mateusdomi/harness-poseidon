-- ARC-05: uma PROPOSTA agrupa mudanças propostas por um agente sobre a arquitetura vigente. Enquanto
-- 'open', vive separada do modelo vigente (os itens propostos ficam com state='proposed' e
-- proposal_id apontando aqui). Aplicar carimba 'applied' + applied_at e exige justificativa em
-- mudança crítica; itens que tocam elemento travado NUNCA são sobrescritos. 'discarded' descarta.
CREATE TABLE architecture_proposals
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    title TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN ('open','applied','discarded')),
    justification TEXT NULL,
    created_at TEXT NOT NULL,
    applied_at TEXT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_proposals_scope
    ON architecture_proposals (tenant_id,project_id,created_at,id);
