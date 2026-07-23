-- ARC-02/ARC-03: metadados de um SISTEMA (elemento kind=system) que alimentam o Mapa Corporativo e o
-- Sistema 360. Heatmaps e seções 360 são DERIVADOS estritamente destes fatos gravados — nada é
-- inventado. O payload tipado completo vive em metadata_json; domain/criticality ficam também em
-- colunas para escopo/ordenção. Uma linha por elemento (o próprio elemento é a identidade).
CREATE TABLE architecture_system_metadata
(
    element_id TEXT PRIMARY KEY CHECK (length(element_id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    domain TEXT NULL,
    criticality TEXT NOT NULL,
    metadata_json TEXT NOT NULL CHECK (json_valid(metadata_json)),
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,element_id)
);

CREATE INDEX ix_architecture_system_metadata_scope
    ON architecture_system_metadata (tenant_id,project_id,element_id);
