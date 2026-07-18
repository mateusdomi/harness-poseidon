CREATE TABLE profile_settings
(
    tenant_id TEXT NOT NULL, id TEXT NOT NULL CHECK(length(id)=26), profile_id TEXT NOT NULL,
    theme TEXT NOT NULL DEFAULT 'system' CHECK(theme IN('dark','light','system')),
    language TEXT NOT NULL DEFAULT 'pt-BR', notifications_enabled INTEGER NOT NULL DEFAULT 1 CHECK(notifications_enabled IN(0,1)),
    muted_categories_json TEXT NOT NULL DEFAULT '[]' CHECK(json_valid(muted_categories_json) AND json_type(muted_categories_json)='array'),
    working_directory TEXT NULL, unsafe_mode_accepted_at TEXT NULL, updated_at TEXT NOT NULL,
    PRIMARY KEY(tenant_id,id), UNIQUE(tenant_id,profile_id),
    FOREIGN KEY(tenant_id,profile_id) REFERENCES local_users(tenant_id,id)
);
CREATE TABLE notifications
(
    tenant_id TEXT NOT NULL, id TEXT NOT NULL CHECK(length(id)=26), profile_id TEXT NOT NULL,
    severity TEXT NOT NULL CHECK(severity IN('info','warning','error','critical')),
    category TEXT NOT NULL CHECK(category IN('system','task','approval','quota','license','chat','workflow')),
    title TEXT NOT NULL, body TEXT NOT NULL, group_key TEXT NULL, dedupe_count INTEGER NOT NULL DEFAULT 1 CHECK(dedupe_count>0),
    status TEXT NOT NULL DEFAULT 'unread' CHECK(status IN('unread','read','muted')), link TEXT NULL,
    created_at TEXT NOT NULL, read_at TEXT NULL, PRIMARY KEY(tenant_id,id),
    FOREIGN KEY(tenant_id,profile_id) REFERENCES local_users(tenant_id,id),
    CHECK((status='read' AND read_at IS NOT NULL) OR status<>'read')
);
CREATE UNIQUE INDEX ux_notifications_unread_group ON notifications(tenant_id,profile_id,group_key)
    WHERE group_key IS NOT NULL AND status='unread';
CREATE INDEX ix_notifications_profile_created ON notifications(tenant_id,profile_id,id);
INSERT INTO profile_settings(tenant_id,id,profile_id,language,updated_at)
SELECT tenant_id,id,id,locale,last_active_at FROM local_users;
