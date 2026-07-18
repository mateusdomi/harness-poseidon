CREATE TABLE chief_states
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    chief_agent_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('idle','working','waiting','error')),
    lease_owner_id TEXT NULL,
    lease_fencing_token INTEGER NOT NULL DEFAULT 0 CHECK (lease_fencing_token >= 0),
    lease_expires_at TEXT NULL,
    session_id TEXT NULL,
    last_digest_json TEXT NULL CHECK (last_digest_json IS NULL OR json_valid(last_digest_json)),
    version INTEGER NOT NULL DEFAULT 1 CHECK (version > 0),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id,project_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id),
    FOREIGN KEY (tenant_id,chief_agent_id) REFERENCES agents(tenant_id,id),
    CHECK ((lease_owner_id IS NULL) = (lease_expires_at IS NULL))
);

CREATE TABLE chief_turn_mailbox
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    user_message_id TEXT NOT NULL,
    state TEXT NOT NULL CHECK (state IN ('pending','processing','completed','failed')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    active_fencing_token INTEGER NULL CHECK (active_fencing_token IS NULL OR active_fencing_token > 0),
    session_id TEXT NULL,
    response_message_id TEXT NULL,
    last_error_code TEXT NULL,
    created_at TEXT NOT NULL,
    started_at TEXT NULL,
    completed_at TEXT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,user_message_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id),
    FOREIGN KEY (tenant_id,conversation_id) REFERENCES conversations(tenant_id,id),
    FOREIGN KEY (tenant_id,user_message_id) REFERENCES conversation_messages(tenant_id,id),
    FOREIGN KEY (tenant_id,response_message_id) REFERENCES conversation_messages(tenant_id,id)
);

CREATE INDEX ix_chief_turn_mailbox_pending
    ON chief_turn_mailbox (tenant_id,project_id,state,created_at,id);
