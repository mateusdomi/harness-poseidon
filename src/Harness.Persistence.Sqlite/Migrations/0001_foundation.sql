CREATE TABLE tenants
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    version INTEGER NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at TEXT NOT NULL
);

CREATE TABLE organizations
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    version INTEGER NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id, name)
);

CREATE TABLE projects
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    organization_id TEXT NOT NULL REFERENCES organizations(id),
    name TEXT NOT NULL CHECK (length(name) BETWEEN 1 AND 200),
    version INTEGER NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at TEXT NOT NULL,
    UNIQUE (organization_id, name)
);

CREATE TABLE local_users
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    display_name TEXT NOT NULL CHECK (length(display_name) BETWEEN 1 AND 200),
    version INTEGER NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at TEXT NOT NULL
);

CREATE TABLE inbox_messages
(
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    message_hash TEXT NOT NULL CHECK (length(message_hash) = 64),
    response_json TEXT NOT NULL,
    processed_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key)
);

CREATE TABLE outbox_messages
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    dispatched_at TEXT NULL,
    attempts INTEGER NOT NULL DEFAULT 0 CHECK (attempts >= 0)
);

CREATE TABLE audit_ledger
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    sequence INTEGER NOT NULL CHECK (sequence > 0),
    previous_hash TEXT NOT NULL CHECK (length(previous_hash) = 64),
    event_hash TEXT NOT NULL CHECK (length(event_hash) = 64),
    event_type TEXT NOT NULL CHECK (length(event_type) BETWEEN 1 AND 200),
    payload_json TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    UNIQUE (tenant_id, sequence),
    UNIQUE (tenant_id, event_hash)
);

CREATE INDEX ix_organizations_tenant ON organizations (tenant_id, id);
CREATE INDEX ix_projects_tenant_organization ON projects (tenant_id, organization_id, id);
CREATE INDEX ix_local_users_tenant ON local_users (tenant_id, id);
CREATE INDEX ix_outbox_pending ON outbox_messages (occurred_at, id) WHERE dispatched_at IS NULL;
CREATE INDEX ix_audit_ledger_tenant_sequence ON audit_ledger (tenant_id, sequence);
