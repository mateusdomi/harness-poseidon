CREATE TABLE conversations
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    project_id TEXT NOT NULL REFERENCES projects(id),
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    state TEXT NOT NULL CHECK (state IN ('active','archived')),
    created_by_profile_id TEXT NOT NULL REFERENCES local_users(id),
    created_at TEXT NOT NULL,
    last_message_at TEXT NULL,
    version INTEGER NOT NULL CHECK (version > 0),
    deleted_at TEXT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id)
);

CREATE TABLE conversation_messages
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    author_role TEXT NOT NULL CHECK (author_role IN ('user','chief','agent','system')),
    author_profile_id TEXT NULL,
    author_agent_id TEXT NULL,
    content TEXT NOT NULL CHECK (length(content) BETWEEN 1 AND 100000),
    token_count INTEGER NULL CHECK (token_count IS NULL OR token_count >= 0),
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,conversation_id) REFERENCES conversations(tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id),
    CHECK ((author_role='user' AND author_profile_id IS NOT NULL AND author_agent_id IS NULL)
        OR (author_role IN ('chief','agent') AND author_profile_id IS NULL AND author_agent_id IS NOT NULL)
        OR (author_role='system' AND author_profile_id IS NULL AND author_agent_id IS NULL))
);

CREATE TABLE chat_turns
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL REFERENCES tenants(id),
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    user_message_id TEXT NOT NULL,
    response_message_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('completed','cancelled','failed')),
    finish_reason TEXT NOT NULL CHECK (finish_reason IN ('stop','cancelled','error')),
    created_at TEXT NOT NULL,
    completed_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    FOREIGN KEY (tenant_id,conversation_id) REFERENCES conversations(tenant_id,id),
    FOREIGN KEY (tenant_id,user_message_id) REFERENCES conversation_messages(tenant_id,id),
    FOREIGN KEY (tenant_id,response_message_id) REFERENCES conversation_messages(tenant_id,id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id)
);

CREATE INDEX ix_conversations_tenant_project_active
    ON conversations (tenant_id,project_id,id) WHERE deleted_at IS NULL;
CREATE INDEX ix_conversation_messages_tenant_conversation
    ON conversation_messages (tenant_id,conversation_id,created_at,id);
CREATE INDEX ix_chat_turns_tenant_conversation
    ON chat_turns (tenant_id,conversation_id,created_at,id);
