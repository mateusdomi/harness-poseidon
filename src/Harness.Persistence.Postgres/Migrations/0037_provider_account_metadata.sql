ALTER TABLE harness.provider_accounts
    ADD COLUMN identity_label varchar(320) NULL CHECK (identity_label IS NULL OR length(identity_label) BETWEEN 1 AND 320),
    ADD COLUMN plan varchar(30) NOT NULL DEFAULT 'unknown' CHECK (plan IN ('unknown', 'free', 'pro', 'team', 'enterprise', 'payAsYouGo', 'local')),
    ADD COLUMN authentication varchar(20) NOT NULL DEFAULT 'apiKey' CHECK (authentication IN ('apiKey', 'oauth', 'local')),
    ADD COLUMN health varchar(20) NOT NULL DEFAULT 'unknown' CHECK (health IN ('unknown', 'healthy', 'degraded', 'unavailable')),
    ADD COLUMN quota_window varchar(20) NOT NULL DEFAULT 'monthly' CHECK (quota_window IN ('daily', 'weekly', 'monthly', 'none')),
    ADD COLUMN quota_resets_at timestamptz NULL,
    ADD COLUMN capabilities_json jsonb NOT NULL DEFAULT '[]'::jsonb CHECK (jsonb_typeof(capabilities_json) = 'array');
