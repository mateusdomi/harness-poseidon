CREATE TABLE harness.chief_states
(
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    chief_agent_id char(26) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('idle', 'working', 'waiting', 'error')),
    lease_owner_id varchar(200) NULL,
    lease_fencing_token bigint NOT NULL DEFAULT 0 CHECK (lease_fencing_token >= 0),
    lease_expires_at timestamptz NULL,
    session_id varchar(200) NULL,
    last_digest_json jsonb NULL,
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, project_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, chief_agent_id) REFERENCES harness.agents(tenant_id, id),
    CHECK ((lease_owner_id IS NULL) = (lease_expires_at IS NULL))
);

CREATE TABLE harness.chief_turn_mailbox
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    conversation_id char(26) NOT NULL,
    user_message_id char(26) NOT NULL,
    state varchar(20) NOT NULL CHECK (state IN ('pending', 'processing', 'completed', 'failed')),
    attempt_count integer NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    active_fencing_token bigint NULL CHECK (active_fencing_token IS NULL OR active_fencing_token > 0),
    session_id varchar(200) NULL,
    response_message_id char(26) NULL,
    last_error_code varchar(200) NULL,
    created_at timestamptz NOT NULL,
    started_at timestamptz NULL,
    completed_at timestamptz NULL,
    UNIQUE (tenant_id, id),
    UNIQUE (tenant_id, user_message_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES harness.conversations(tenant_id, id),
    FOREIGN KEY (tenant_id, user_message_id) REFERENCES harness.conversation_messages(tenant_id, id),
    FOREIGN KEY (tenant_id, response_message_id) REFERENCES harness.conversation_messages(tenant_id, id)
);

CREATE INDEX ix_chief_turn_mailbox_pending
    ON harness.chief_turn_mailbox (tenant_id, project_id, state, created_at, id);
