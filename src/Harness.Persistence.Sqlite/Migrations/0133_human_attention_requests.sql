-- Human Attention Loop: a pergunta que EXIGE o humano deixa de morrer no chat.
--
-- O que existia: card escalado era anunciado UMA vez na conversa; se o dono estivesse longe da
-- máquina, ninguém insistia, nada media o tempo de resposta e nenhum canal externo era acionado
-- pelo PRODUTO (o Telegram bidirecional existia como canal de conversa, não como alarme).
-- Este registro é o SLA de atenção humana: criado somente por ASK genuíno, com escopo de
-- bloqueio declarado, lembretes DETERMINÍSTICOS (política em código, nunca "a Bruna achou que
-- já passou tempo demais") e a métrica que a prova empresarial de 20/08 precisa exibir
-- (decisões humanas exigidas × tempo mediano de resposta).
CREATE TABLE human_attention_requests
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    project_id TEXT NOT NULL CHECK (length(project_id) = 26),
    conversation_id TEXT NULL,
    card_id TEXT NULL,
    graph_node_id TEXT NULL,
    question TEXT NOT NULL CHECK (length(question) BETWEEN 1 AND 4000),
    reason TEXT NOT NULL,
    classification TEXT NOT NULL CHECK (classification IN ('ask')),
    severity TEXT NOT NULL CHECK (severity IN ('low', 'medium', 'high', 'critical')),
    blocking_scope TEXT NOT NULL,
    status TEXT NOT NULL CHECK (status IN (
        'open', 'notified', 'acknowledged', 'answered', 'expired', 'superseded')),
    source_agent TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    reminder_count INTEGER NOT NULL DEFAULT 0 CHECK (reminder_count >= 0),
    channel_status TEXT NOT NULL DEFAULT 'none',
    answer TEXT NULL,
    created_at TEXT NOT NULL,
    acknowledged_at TEXT NULL,
    answered_at TEXT NULL,
    next_action_at TEXT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, correlation_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

CREATE INDEX ix_human_attention_open
    ON human_attention_requests (tenant_id, status, next_action_at);
CREATE INDEX ix_human_attention_project
    ON human_attention_requests (tenant_id, project_id, status);
