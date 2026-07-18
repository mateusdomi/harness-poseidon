CREATE TABLE licenses
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    tenant_id TEXT NOT NULL UNIQUE REFERENCES tenants(id),
    state TEXT NOT NULL CHECK (state IN ('active','unlicensed')),
    plan TEXT NOT NULL CHECK (length(plan) BETWEEN 1 AND 100),
    device_id TEXT NOT NULL CHECK (length(device_id) BETWEEN 1 AND 200),
    device_name TEXT NOT NULL CHECK (length(device_name) BETWEEN 1 AND 200),
    expires_at TEXT NULL,
    grace_period_ends_at TEXT NULL,
    offline_mode INTEGER NOT NULL CHECK (offline_mode IN (0,1)),
    last_validated_at TEXT NULL,
    activation_key_hash TEXT NULL CHECK (activation_key_hash IS NULL OR length(activation_key_hash)=64),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE TABLE entitlements
(
    id TEXT PRIMARY KEY CHECK (length(id)=26),
    tenant_id TEXT NOT NULL,
    license_id TEXT NOT NULL,
    key TEXT NOT NULL CHECK (length(key) BETWEEN 1 AND 200),
    description TEXT NOT NULL CHECK (length(description) BETWEEN 1 AND 500),
    included INTEGER NOT NULL CHECK (included IN (0,1)),
    numeric_limit INTEGER NULL CHECK (numeric_limit IS NULL OR numeric_limit > 0),
    UNIQUE (tenant_id,key),
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,license_id) REFERENCES licenses(tenant_id,id) ON DELETE CASCADE
);

CREATE INDEX ix_entitlements_license ON entitlements (tenant_id,license_id,id);
