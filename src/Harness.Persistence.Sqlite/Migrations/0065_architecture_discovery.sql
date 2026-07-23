-- ARC-06: descoberta de sistemas existentes. Cada linha é UMA informação descoberta a partir de uma
-- fonte (repo/docs/OpenAPI/schema/Dockerfile/pipeline/IaC/inventário/planilha/diagrama/entrevista),
-- SEMPRE com confiança + evidência + perguntas pendentes explícitas — nada é afirmado sem isso. Fica
-- 'open' até um humano confirmar/rejeitar; o payload tipado completo (evidência, perguntas) vive no
-- payload_json e as colunas-chave existem para escopo/filtro. Não sobrescreve o modelo vigente: uma
-- descoberta é um candidato a fato, não um fato do modelo.
CREATE TABLE architecture_discoveries
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    system_id TEXT NULL,
    source_kind TEXT NOT NULL,
    confidence TEXT NOT NULL CHECK (confidence IN ('low','medium','high')),
    status TEXT NOT NULL CHECK (status IN ('open','confirmed','rejected')),
    payload_json TEXT NOT NULL CHECK (json_valid(payload_json)),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_discoveries_scope
    ON architecture_discoveries (tenant_id,project_id,system_id,id);
