-- PLAT-02: notas de contexto do Chief externalizadas pela IContextStrategy. A estratégia mantém a
-- janela de trabalho limitada por limiar (compactação + limpeza de tool-result); os fatos/decisões
-- CRÍTICOS não vivem na compactação, e sim aqui — memória durável keyed por
-- (tenant, project, [conversation]) que sobrevive à compactação e ao reinício.
--
-- IDEMPOTÊNCIA: a unicidade por (tenant, project, conversation, source_item_id) garante que
-- reprocessar o mesmo turno NÃO duplique notas (INSERT OR IGNORE no store). O escopo referencia o
-- projeto (durável); a conversa é opcional, permitindo notas de nível de projeto.
CREATE TABLE chief_context_notes
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    conversation_id TEXT NULL,
    turn_id TEXT NULL,
    source_item_id TEXT NOT NULL,
    role TEXT NOT NULL,
    content TEXT NOT NULL,
    token_estimate INTEGER NOT NULL,
    sequence INTEGER NOT NULL,
    created_at TEXT NOT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,project_id,conversation_id,source_item_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES projects(tenant_id,id)
);

CREATE INDEX ix_chief_context_notes_scope
    ON chief_context_notes (tenant_id,project_id,conversation_id,sequence,id);
