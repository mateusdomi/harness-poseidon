-- ARC-01: uma VIEW (diagrama C4/ArchiMate) é uma SELEÇÃO/FILTRO nomeada sobre o modelo, NUNCA uma
-- imagem. Guarda apenas referências (ids selecionados) e critérios de filtro; o subgrafo concreto é
-- resolvido on-demand a partir do modelo vigente. O mesmo elemento é REUSADO por várias views.
CREATE TABLE architecture_views
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    notation TEXT NOT NULL,
    element_ids_json TEXT NOT NULL CHECK (json_valid(element_ids_json) AND json_type(element_ids_json) = 'array'),
    relationship_ids_json TEXT NOT NULL CHECK (json_valid(relationship_ids_json) AND json_type(relationship_ids_json) = 'array'),
    filter_kinds_json TEXT NOT NULL CHECK (json_valid(filter_kinds_json) AND json_type(filter_kinds_json) = 'array'),
    filter_tags_json TEXT NOT NULL CHECK (json_valid(filter_tags_json) AND json_type(filter_tags_json) = 'array'),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_views_scope
    ON architecture_views (tenant_id,project_id,id);
