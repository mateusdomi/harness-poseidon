CREATE TABLE harness.licenses
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL UNIQUE REFERENCES harness.tenants(id),
    state varchar(20) NOT NULL CHECK (state IN ('active', 'unlicensed')),
    plan varchar(100) NOT NULL CHECK (length(plan) > 0),
    device_id varchar(200) NOT NULL CHECK (length(device_id) > 0),
    device_name varchar(200) NOT NULL CHECK (length(device_name) > 0),
    expires_at timestamptz NULL,
    grace_period_ends_at timestamptz NULL,
    offline_mode boolean NOT NULL,
    last_validated_at timestamptz NULL,
    activation_key_hash char(64) NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id)
);

CREATE TABLE harness.entitlements
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    license_id char(26) NOT NULL,
    key varchar(200) NOT NULL CHECK (length(key) > 0),
    description varchar(500) NOT NULL CHECK (length(description) > 0),
    included boolean NOT NULL,
    numeric_limit integer NULL CHECK (numeric_limit IS NULL OR numeric_limit > 0),
    UNIQUE (tenant_id, key),
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, license_id) REFERENCES harness.licenses(tenant_id, id) ON DELETE CASCADE
);

CREATE INDEX ix_entitlements_license ON harness.entitlements (tenant_id, license_id, id);
