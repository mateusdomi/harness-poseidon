CREATE TABLE harness.visual_reference_assets
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    reference_id char(26) NOT NULL,
    file_name varchar(200) NOT NULL CHECK (length(file_name) > 0),
    content_type varchar(200) NOT NULL CHECK (length(content_type) > 0),
    size_bytes bigint NOT NULL CHECK (size_bytes > 0),
    sha256 char(64) NOT NULL,
    storage_path text NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, reference_id, sha256)
);

CREATE INDEX ix_visual_reference_assets_reference
    ON harness.visual_reference_assets (tenant_id, reference_id, id);
