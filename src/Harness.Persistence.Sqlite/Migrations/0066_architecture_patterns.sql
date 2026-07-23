-- ARC-08: padrões & decisões. Uma tabela para o acervo corporativo — ADRs (kind='adr') e padrões
-- reutilizáveis (kind='pattern'), o mesmo problema resolvido de forma consistente. O corpo tipado
-- (contexto, decisão/solução, consequências, tags, supersede, link para Documents/ADR) vive no
-- payload_json; kind/status ficam em colunas para filtro. Aditivo; não acopla a Documents.
CREATE TABLE architecture_patterns
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('adr','pattern')),
    status TEXT NOT NULL,
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_patterns_scope
    ON architecture_patterns (tenant_id,project_id,kind,id);
