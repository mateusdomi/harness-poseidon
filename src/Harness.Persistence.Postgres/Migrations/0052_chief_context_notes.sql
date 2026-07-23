-- PLAT-02: notas de contexto do Chief externalizadas pela IContextStrategy. A estratégia mantém a
-- janela de trabalho limitada por limiar (compactação + limpeza de tool-result); os fatos/decisões
-- CRÍTICOS não vivem na compactação, e sim aqui — memória durável keyed por
-- (tenant, project, [conversation]) que sobrevive à compactação e ao reinício.
--
-- IDEMPOTÊNCIA: a unicidade por (tenant, project, conversation, source_item_id) garante que
-- reprocessar o mesmo turno NÃO duplique notas (ON CONFLICT DO NOTHING no store). O escopo
-- referencia o projeto (durável); a conversa é opcional, permitindo notas de nível de projeto.
CREATE TABLE harness.chief_context_notes
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    conversation_id char(26) NULL,
    turn_id char(26) NULL,
    source_item_id text NOT NULL,
    role text NOT NULL,
    content text NOT NULL,
    token_estimate integer NOT NULL,
    sequence bigint NOT NULL,
    created_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id),
    UNIQUE (tenant_id,project_id,conversation_id,source_item_id),
    FOREIGN KEY (tenant_id,project_id) REFERENCES harness.projects(tenant_id,id)
);

CREATE INDEX ix_chief_context_notes_scope
    ON harness.chief_context_notes (tenant_id,project_id,conversation_id,sequence,id);
