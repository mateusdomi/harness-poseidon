CREATE TABLE harness.profile_settings
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    profile_id char(26) NOT NULL,
    theme varchar(20) NOT NULL DEFAULT 'system' CHECK (theme IN ('dark', 'light', 'system')),
    language varchar(35) NOT NULL DEFAULT 'pt-BR',
    notifications_enabled boolean NOT NULL DEFAULT true,
    muted_categories_json jsonb NOT NULL DEFAULT '[]'
        CHECK (jsonb_typeof(muted_categories_json) = 'array'),
    working_directory text NULL,
    unsafe_mode_accepted_at timestamptz NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, profile_id),
    FOREIGN KEY (tenant_id, profile_id) REFERENCES harness.local_users(tenant_id, id)
);

CREATE TABLE harness.notifications
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    profile_id char(26) NOT NULL,
    severity varchar(20) NOT NULL CHECK (severity IN ('info', 'warning', 'error', 'critical')),
    category varchar(20) NOT NULL CHECK
        (category IN ('system', 'task', 'approval', 'quota', 'license', 'chat', 'workflow')),
    title varchar(200) NOT NULL,
    body varchar(4000) NOT NULL,
    group_key varchar(200) NULL,
    dedupe_count integer NOT NULL DEFAULT 1 CHECK (dedupe_count > 0),
    status varchar(20) NOT NULL DEFAULT 'unread' CHECK (status IN ('unread', 'read', 'muted')),
    link varchar(2000) NULL,
    created_at timestamptz NOT NULL,
    read_at timestamptz NULL,
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, profile_id) REFERENCES harness.local_users(tenant_id, id),
    CHECK ((status = 'read' AND read_at IS NOT NULL) OR status <> 'read')
);

CREATE UNIQUE INDEX ux_notifications_unread_group
    ON harness.notifications (tenant_id, profile_id, group_key)
    WHERE group_key IS NOT NULL AND status = 'unread';
CREATE INDEX ix_notifications_profile_created
    ON harness.notifications (tenant_id, profile_id, id);

INSERT INTO harness.profile_settings (tenant_id, id, profile_id, language, updated_at)
SELECT tenant_id, id, id, locale, COALESCE(last_active_at, created_at) FROM harness.local_users;
