CREATE TABLE harness.conversations
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    project_id char(26) NOT NULL REFERENCES harness.projects(id),
    title varchar(200) NOT NULL CHECK (length(title) BETWEEN 1 AND 200),
    state varchar(20) NOT NULL CHECK (state IN ('active', 'archived')),
    created_by_profile_id char(26) NOT NULL REFERENCES harness.local_users(id),
    created_at timestamptz NOT NULL,
    last_message_at timestamptz NULL,
    version bigint NOT NULL CHECK (version > 0),
    deleted_at timestamptz NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

CREATE TABLE harness.conversation_messages
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    project_id char(26) NOT NULL,
    conversation_id char(26) NOT NULL,
    author_role varchar(20) NOT NULL CHECK (author_role IN ('user', 'chief', 'agent', 'system')),
    author_profile_id char(26) NULL,
    author_agent_id char(26) NULL,
    content varchar(100000) NOT NULL CHECK (length(content) BETWEEN 1 AND 100000),
    token_count integer NULL CHECK (token_count IS NULL OR token_count >= 0),
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES harness.conversations(tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    CHECK ((author_role = 'user' AND author_profile_id IS NOT NULL AND author_agent_id IS NULL)
        OR (author_role IN ('chief', 'agent') AND author_profile_id IS NULL AND author_agent_id IS NOT NULL)
        OR (author_role = 'system' AND author_profile_id IS NULL AND author_agent_id IS NULL))
);

CREATE TABLE harness.chat_turns
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL REFERENCES harness.tenants(id),
    project_id char(26) NOT NULL,
    conversation_id char(26) NOT NULL,
    user_message_id char(26) NOT NULL,
    response_message_id char(26) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('completed', 'cancelled', 'failed')),
    finish_reason varchar(20) NOT NULL CHECK (finish_reason IN ('stop', 'cancelled', 'error')),
    created_at timestamptz NOT NULL,
    completed_at timestamptz NOT NULL,
    UNIQUE (tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES harness.conversations(tenant_id, id),
    FOREIGN KEY (tenant_id, user_message_id) REFERENCES harness.conversation_messages(tenant_id, id),
    FOREIGN KEY (tenant_id, response_message_id) REFERENCES harness.conversation_messages(tenant_id, id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

CREATE INDEX ix_conversations_tenant_project_active
    ON harness.conversations (tenant_id, project_id, id) WHERE deleted_at IS NULL;
CREATE INDEX ix_conversation_messages_tenant_conversation
    ON harness.conversation_messages (tenant_id, conversation_id, created_at, id);
CREATE INDEX ix_chat_turns_tenant_conversation
    ON harness.chat_turns (tenant_id, conversation_id, created_at, id);
