CREATE TABLE harness.tenants
(
    id char(26) PRIMARY KEY,
    name varchar(200) NOT NULL,
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at timestamptz NOT NULL,
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.organizations
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    name varchar(200) NOT NULL,
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id, name),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.projects
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    organization_id char(26) NOT NULL REFERENCES harness.organizations(id),
    name varchar(200) NOT NULL,
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at timestamptz NOT NULL,
    UNIQUE (organization_id, name),
    CHECK (length(trim(name)) > 0)
);

CREATE TABLE harness.local_users
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    display_name varchar(200) NOT NULL,
    version bigint NOT NULL DEFAULT 0 CHECK (version >= 0),
    created_at timestamptz NOT NULL,
    CHECK (length(trim(display_name)) > 0)
);

CREATE TABLE harness.inbox_messages
(
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    idempotency_key varchar(200) NOT NULL,
    message_hash char(64) NOT NULL,
    response_json jsonb NOT NULL,
    processed_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key),
    CHECK (length(trim(idempotency_key)) > 0)
);

CREATE TABLE harness.outbox_messages
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    event_type varchar(200) NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    dispatched_at timestamptz NULL,
    attempts integer NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    CHECK (length(trim(event_type)) > 0)
);

CREATE TABLE harness.audit_ledger
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    previous_hash char(64) NOT NULL,
    event_hash char(64) NOT NULL,
    event_type varchar(200) NOT NULL,
    payload_json jsonb NOT NULL,
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id, sequence),
    UNIQUE (tenant_id, event_hash),
    CHECK (length(trim(event_type)) > 0)
);

CREATE INDEX ix_organizations_tenant ON harness.organizations (tenant_id, id);
CREATE INDEX ix_projects_tenant_organization ON harness.projects (tenant_id, organization_id, id);
CREATE INDEX ix_local_users_tenant ON harness.local_users (tenant_id, id);
CREATE INDEX ix_outbox_pending ON harness.outbox_messages (occurred_at, id) WHERE dispatched_at IS NULL;
CREATE INDEX ix_audit_ledger_tenant_sequence ON harness.audit_ledger (tenant_id, sequence);
