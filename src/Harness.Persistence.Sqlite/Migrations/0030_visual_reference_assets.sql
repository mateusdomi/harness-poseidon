CREATE TABLE visual_reference_assets
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    reference_id TEXT NOT NULL,
    file_name TEXT NOT NULL CHECK (length(file_name) BETWEEN 1 AND 200),
    content_type TEXT NOT NULL CHECK (length(content_type) BETWEEN 1 AND 200),
    size_bytes INTEGER NOT NULL CHECK (size_bytes > 0),
    sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
    storage_path TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, reference_id, sha256)
);

CREATE INDEX ix_visual_reference_assets_reference
    ON visual_reference_assets (tenant_id, reference_id, id);
