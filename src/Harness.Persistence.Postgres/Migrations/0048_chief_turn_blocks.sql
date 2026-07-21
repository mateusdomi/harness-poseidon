-- ADR-019/C2: um turno recusado por prontidão não é um item de execução — ele nunca entra no
-- mailbox. A mensagem humana continua persistida e o bloqueio vira registro durável e
-- auditável, com bloqueadores tipados (códigos, nunca texto livre de domínio).
-- A unicidade por mensagem garante que o retry não duplique bloqueio nem evento.
CREATE TABLE harness.chief_turn_blocks
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    conversation_id char(26) NOT NULL,
    user_message_id char(26) NOT NULL,
    readiness_state text NOT NULL,
    blockers_json jsonb NOT NULL,
    next_actions_json jsonb NOT NULL,
    correlation_id text NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,user_message_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES harness.projects(tenant_id,id),
    FOREIGN KEY (tenant_id,conversation_id) REFERENCES harness.conversations(tenant_id,id),
    FOREIGN KEY (tenant_id,user_message_id) REFERENCES harness.conversation_messages(tenant_id,id)
);

CREATE INDEX ix_chief_turn_blocks_conversation
    ON harness.chief_turn_blocks (tenant_id,conversation_id,created_at,id);
