-- ARC-02/ARC-03: metadados de um SISTEMA (elemento kind=system) que alimentam o Mapa Corporativo e o
-- Sistema 360. Heatmaps e seções 360 são DERIVADOS estritamente destes fatos — nada é inventado.
-- O payload tipado completo vive em metadata_json; domain/criticality também em colunas para escopo.
CREATE TABLE harness.architecture_system_metadata
(
    element_id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    domain text NULL,
    criticality text NOT NULL,
    metadata_json jsonb NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,element_id)
);

CREATE INDEX ix_architecture_system_metadata_scope
    ON harness.architecture_system_metadata (tenant_id,project_id,element_id);
