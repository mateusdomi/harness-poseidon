CREATE TABLE harness.signed_licenses
(
    tenant_id char(26) NOT NULL,
    license_id char(26) NOT NULL,
    licensee varchar(200) NOT NULL CHECK (length(licensee) > 0),
    device_fingerprint text NULL,
    issued_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    grace_days integer NOT NULL CHECK (grace_days BETWEEN 0 AND 90),
    entitlements_json jsonb NOT NULL CHECK (jsonb_typeof(entitlements_json) = 'array'),
    document_json json NOT NULL,
    signature text NOT NULL,
    activated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, license_id)
);

CREATE INDEX ix_signed_licenses_activation
    ON harness.signed_licenses (tenant_id, activated_at);

CREATE TABLE harness.license_revocations
(
    tenant_id char(26) NOT NULL,
    license_id char(26) NOT NULL,
    revoked_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, license_id)
);
