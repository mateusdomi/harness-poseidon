-- Amplia o conjunto fechado de canais com os adapters WhatsApp e e-mail e registra
-- a última entrada recebida por vínculo (last_inbound_at), insumo durável do
-- roteamento de saída para o último canal ativo da conversa. SQLite não altera
-- CHECK existente: rebuild da tabela preservando dados, chaves e unicidade.
CREATE TABLE channel_links_omnichannel
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL CHECK (length(id) = 26),
    kind TEXT NOT NULL CHECK (kind IN ('terminal', 'telegram', 'teams', 'whatsapp', 'email')),
    external_identity TEXT NOT NULL CHECK (length(external_identity) BETWEEN 1 AND 200),
    profile_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    linked_at TEXT NOT NULL,
    last_inbound_at TEXT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, kind, external_identity),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES conversations(tenant_id, id)
);

INSERT INTO channel_links_omnichannel
(
    tenant_id,
    id,
    kind,
    external_identity,
    profile_id,
    project_id,
    conversation_id,
    linked_at,
    last_inbound_at
)
SELECT
    tenant_id,
    id,
    kind,
    external_identity,
    profile_id,
    project_id,
    conversation_id,
    linked_at,
    NULL
FROM channel_links;

DROP TABLE channel_links;
ALTER TABLE channel_links_omnichannel RENAME TO channel_links;
