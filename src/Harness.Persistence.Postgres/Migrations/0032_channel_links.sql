CREATE TABLE harness.channel_links
(
    tenant_id char(26) NOT NULL,
    id char(26) NOT NULL,
    kind varchar(20) NOT NULL CHECK (kind IN ('terminal', 'telegram', 'teams')),
    external_identity varchar(200) NOT NULL CHECK (length(external_identity) > 0),
    profile_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    conversation_id char(26) NOT NULL,
    linked_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, kind, external_identity),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES harness.conversations(tenant_id, id)
);
