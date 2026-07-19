ALTER TABLE provider_accounts ADD COLUMN identity_label TEXT NULL CHECK(identity_label IS NULL OR length(identity_label) BETWEEN 1 AND 320);
ALTER TABLE provider_accounts ADD COLUMN plan TEXT NOT NULL DEFAULT 'unknown' CHECK(plan IN('unknown','free','pro','team','enterprise','payAsYouGo','local'));
ALTER TABLE provider_accounts ADD COLUMN authentication TEXT NOT NULL DEFAULT 'apiKey' CHECK(authentication IN('apiKey','oauth','local'));
ALTER TABLE provider_accounts ADD COLUMN health TEXT NOT NULL DEFAULT 'unknown' CHECK(health IN('unknown','healthy','degraded','unavailable'));
ALTER TABLE provider_accounts ADD COLUMN quota_window TEXT NOT NULL DEFAULT 'monthly' CHECK(quota_window IN('daily','weekly','monthly','none'));
ALTER TABLE provider_accounts ADD COLUMN quota_resets_at TEXT NULL;
ALTER TABLE provider_accounts ADD COLUMN capabilities_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(capabilities_json) AND json_type(capabilities_json)='array');
