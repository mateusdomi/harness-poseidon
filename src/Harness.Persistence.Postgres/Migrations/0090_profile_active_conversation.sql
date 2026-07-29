-- Paridade com o SQLite: a última conversa aberta é guardada por perfil E por projeto, no
-- servidor. Por projeto porque a conversa de um projeto não é a continuação da de outro; no
-- servidor porque a promessa é continuar de onde parou, não de onde parou naquele navegador.
CREATE TABLE harness.profile_active_conversations
(
    tenant_id text NOT NULL,
    profile_id text NOT NULL,
    project_id text NOT NULL,
    conversation_id text NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, profile_id, project_id)
);

CREATE INDEX ix_profile_active_conversations_profile
    ON harness.profile_active_conversations (tenant_id, profile_id, updated_at);

ALTER TABLE harness.profile_active_conversations ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.profile_active_conversations FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.profile_active_conversations
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
