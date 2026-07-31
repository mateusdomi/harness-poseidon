-- Fase 0C1/0C2 (BR-003): o merge deixa de ser um efeito Git sem registro e passa a ter INTENÇÃO
-- durável, coordenação por repositório e reconciliação.
--
-- ANTES: `TaskIntegrationService` executava `git merge` e SÓ DEPOIS atualizava a cadeia durável.
-- Uma falha de banco entre as duas coisas deixava o código integrado e o card em `approved` para
-- sempre — divergência silenciosa entre o que existe no Git e o que o produto acredita. E a
-- coordenação era um `SemaphoreSlim` em memória: protegia um processo, não o repositório.
--
-- Não se tenta uma transação distribuída entre Git e SQL, porque ela não existe. Registra-se a
-- INTENÇÃO antes do efeito, para que qualquer desfecho seja reconhecível depois.
CREATE TABLE merge_intents
(
    tenant_id TEXT NOT NULL,
    merge_intent_id TEXT NOT NULL CHECK (length(merge_intent_id) = 26),
    repository_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    card_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    source_branch TEXT NOT NULL,
    target_branch TEXT NOT NULL,
    expected_source_sha TEXT NULL,
    expected_base_sha TEXT NULL,
    state TEXT NOT NULL
        CHECK (state IN ('pending', 'merging', 'merged', 'failed', 'aborted')),
    owner_id TEXT NULL,
    fencing_token INTEGER NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
    lease_expires_at TEXT NULL,
    result_sha TEXT NULL,
    board_settled INTEGER NOT NULL DEFAULT 0 CHECK (board_settled IN (0, 1)),
    last_error TEXT NULL CHECK (last_error IS NULL OR length(last_error) <= 4000),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, merge_intent_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects (tenant_id, id)
);

-- UM merge ativo por repositório, garantido pelo BANCO. Um semáforo em memória protegia um
-- processo; isto protege o repositório, que é o recurso de verdade disputado.
CREATE UNIQUE INDEX ux_merge_intents_active_repository
    ON merge_intents (tenant_id, repository_id)
    WHERE state = 'merging';

-- Um card tem uma intenção por tentativa: retry reusa a mesma, não abre outra.
CREATE UNIQUE INDEX ux_merge_intents_attempt
    ON merge_intents (tenant_id, attempt_id);

CREATE INDEX ix_merge_intents_unsettled
    ON merge_intents (updated_at, tenant_id, merge_intent_id)
    WHERE state IN ('pending', 'merging') OR board_settled = 0;
