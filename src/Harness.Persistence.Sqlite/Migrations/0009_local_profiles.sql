ALTER TABLE local_users ADD COLUMN email TEXT NULL;
ALTER TABLE local_users ADD COLUMN avatar_url TEXT NULL;
ALTER TABLE local_users ADD COLUMN locale TEXT NOT NULL DEFAULT 'pt-BR';
ALTER TABLE local_users ADD COLUMN last_active_at TEXT NULL;

UPDATE local_users SET last_active_at = created_at WHERE last_active_at IS NULL;

CREATE UNIQUE INDEX ux_local_users_tenant_email
    ON local_users (tenant_id, lower(email))
    WHERE email IS NOT NULL;
