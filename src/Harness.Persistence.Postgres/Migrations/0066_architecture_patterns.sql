-- ARC-08: padrões & decisões. Uma tabela para o acervo corporativo — ADRs (kind='adr') e padrões
-- reutilizáveis (kind='pattern'), o mesmo problema resolvido de forma consistente. O corpo tipado
-- (contexto, decisão/solução, consequências, tags, supersede, link para Documents/ADR) vive no
-- payload_json (jsonb); kind/status ficam em colunas para filtro. Aditivo; não acopla a Documents.
CREATE TABLE harness.architecture_patterns
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    kind text NOT NULL CHECK (kind IN ('adr','pattern')),
    status text NOT NULL,
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_patterns_scope
    ON harness.architecture_patterns (tenant_id,project_id,kind,id);
