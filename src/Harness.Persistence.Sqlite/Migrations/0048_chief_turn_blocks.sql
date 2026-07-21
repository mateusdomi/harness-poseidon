-- ADR-019/C2: um turno recusado por prontidão não é um item de execução — ele nunca entra no
-- mailbox. A mensagem humana continua persistida e o bloqueio vira registro durável e
-- auditável, com bloqueadores tipados (códigos, nunca texto livre de domínio).
-- A unicidade por mensagem garante que o retry não duplique bloqueio nem evento.
CREATE TABLE chief_turn_blocks
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    user_message_id TEXT NOT NULL,
    readiness_state TEXT NOT NULL,
    blockers_json TEXT NOT NULL CHECK (json_valid(blockers_json)),
    next_actions_json TEXT NOT NULL CHECK (json_valid(next_actions_json)),
    correlation_id TEXT NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,user_message_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id),
    FOREIGN KEY (tenant_id,conversation_id) REFERENCES conversations(tenant_id,id),
    FOREIGN KEY (tenant_id,user_message_id) REFERENCES conversation_messages(tenant_id,id)
);

CREATE INDEX ix_chief_turn_blocks_conversation
    ON chief_turn_blocks (tenant_id,conversation_id,created_at,id);
