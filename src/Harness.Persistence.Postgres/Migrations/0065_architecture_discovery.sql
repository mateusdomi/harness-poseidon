-- ARC-06: descoberta de sistemas existentes. Cada linha é UMA informação descoberta a partir de uma
-- fonte (repo/docs/OpenAPI/schema/Dockerfile/pipeline/IaC/inventário/planilha/diagrama/entrevista),
-- SEMPRE com confiança + evidência + perguntas pendentes explícitas — nada é afirmado sem isso. Fica
-- 'open' até um humano confirmar/rejeitar; o payload tipado completo vive no payload_json (jsonb) e as
-- colunas-chave existem para escopo/filtro. Uma descoberta é um candidato a fato, não um fato do modelo.
CREATE TABLE harness.architecture_discoveries
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    system_id char(26) NULL,
    source_kind text NOT NULL,
    confidence text NOT NULL CHECK (confidence IN ('low','medium','high')),
    status text NOT NULL CHECK (status IN ('open','confirmed','rejected')),
    payload_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_discoveries_scope
    ON harness.architecture_discoveries (tenant_id,project_id,system_id,id);
