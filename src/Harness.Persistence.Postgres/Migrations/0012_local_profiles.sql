ALTER TABLE harness.local_users ADD COLUMN email varchar(320) NULL;
ALTER TABLE harness.local_users ADD COLUMN avatar_url text NULL;
ALTER TABLE harness.local_users ADD COLUMN locale varchar(20) NOT NULL DEFAULT 'pt-BR';
ALTER TABLE harness.local_users ADD COLUMN last_active_at timestamptz NULL;

UPDATE harness.local_users SET last_active_at = created_at WHERE last_active_at IS NULL;

CREATE UNIQUE INDEX ux_local_users_tenant_email
    ON harness.local_users (tenant_id, lower(email))
    WHERE email IS NOT NULL;
