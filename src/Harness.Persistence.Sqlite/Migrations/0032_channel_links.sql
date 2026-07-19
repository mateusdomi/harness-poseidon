CREATE TABLE channel_links
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    kind TEXT NOT NULL CHECK (kind IN ('terminal', 'telegram', 'teams')),
    external_identity TEXT NOT NULL CHECK (length(external_identity) BETWEEN 1 AND 200),
    profile_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    linked_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, kind, external_identity),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES conversations(tenant_id, id)
);
