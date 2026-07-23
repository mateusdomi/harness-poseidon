-- ARC-01: uma VIEW (diagrama C4/ArchiMate) é uma SELEÇÃO/FILTRO nomeada sobre o modelo, NUNCA uma
-- imagem. Guarda apenas referências e critérios de filtro; o subgrafo é resolvido on-demand.
CREATE TABLE harness.architecture_views
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    name text NOT NULL,
    description text NOT NULL,
    notation text NOT NULL,
    element_ids_json jsonb NOT NULL,
    relationship_ids_json jsonb NOT NULL,
    filter_kinds_json jsonb NOT NULL,
    filter_tags_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    CHECK (jsonb_typeof(element_ids_json) = 'array'),
    CHECK (jsonb_typeof(relationship_ids_json) = 'array'),
    CHECK (jsonb_typeof(filter_kinds_json) = 'array'),
    CHECK (jsonb_typeof(filter_tags_json) = 'array')
);

CREATE INDEX ix_architecture_views_scope
    ON harness.architecture_views (tenant_id,project_id,id);
