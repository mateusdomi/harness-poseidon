-- ÚLTIMA CONVERSA ABERTA, POR PERFIL E POR PROJETO (D16).
--
-- Bug homologado: o dono abria uma conversa, navegava para outra tela e, ao voltar ao Chat,
-- encontrava a tela inicial em vez da conversa onde estava. O efeito prático é pior do que a
-- inconveniência: ele perde o fio do que estava combinando com a Bruna e recomeça a explicar.
--
-- Por que PERFIL + PROJETO, e não só perfil: o dono conversa sobre um projeto de cada vez, e a
-- conversa do projeto A não é a continuação da conversa do projeto B. Guardar só por perfil faria
-- o Chat abrir a conversa errada logo depois de trocar de projeto — trocaria um bug por outro.
--
-- Por que no servidor, e não no navegador: o dono usa a plataforma de mais de um lugar, e a
-- promessa é continuar de onde parou, não continuar de onde parou naquele navegador.
CREATE TABLE profile_active_conversations
(
    tenant_id TEXT NOT NULL,
    profile_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    -- Uma conversa ativa por (perfil, projeto): duas deixariam a restauração ambígua.
    PRIMARY KEY (tenant_id, profile_id, project_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    FOREIGN KEY (tenant_id, conversation_id) REFERENCES conversations(tenant_id, id)
);

CREATE INDEX ix_profile_active_conversations_profile
    ON profile_active_conversations (tenant_id, profile_id, updated_at);
