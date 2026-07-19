CREATE TABLE signed_licenses
(
    tenant_id TEXT NOT NULL,
    license_id TEXT NOT NULL CHECK (length(license_id) = 26),
    licensee TEXT NOT NULL CHECK (length(licensee) BETWEEN 1 AND 200),
    device_fingerprint TEXT NULL,
    issued_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    grace_days INTEGER NOT NULL CHECK (grace_days BETWEEN 0 AND 90),
    entitlements_json TEXT NOT NULL
        CHECK (json_valid(entitlements_json) AND json_type(entitlements_json) = 'array'),
    document_json TEXT NOT NULL CHECK (json_valid(document_json)),
    signature TEXT NOT NULL,
    activated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, license_id)
);

CREATE INDEX ix_signed_licenses_activation
    ON signed_licenses (tenant_id, activated_at);

CREATE TABLE license_revocations
(
    tenant_id TEXT NOT NULL,
    license_id TEXT NOT NULL CHECK (length(license_id) = 26),
    revoked_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, license_id)
);
