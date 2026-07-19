CREATE TABLE solicitation_attachments
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    solicitation_id TEXT NOT NULL,
    file_name TEXT NOT NULL CHECK (length(file_name) BETWEEN 1 AND 200),
    content_type TEXT NOT NULL CHECK (length(content_type) BETWEEN 1 AND 200),
    size_bytes INTEGER NOT NULL CHECK (size_bytes > 0),
    sha256 TEXT NOT NULL CHECK (length(sha256) = 64),
    state TEXT NOT NULL CHECK (state IN ('quarantined', 'accepted')),
    storage_path TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, solicitation_id, sha256),
    FOREIGN KEY (solicitation_id) REFERENCES solicitations(id)
);

CREATE INDEX ix_solicitation_attachments_solicitation
    ON solicitation_attachments (tenant_id, solicitation_id, id);
