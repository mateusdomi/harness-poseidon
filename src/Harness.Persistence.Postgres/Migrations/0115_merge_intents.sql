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
CREATE TABLE harness.merge_intents
(
    tenant_id text NOT NULL,
    merge_intent_id char(26) NOT NULL,
    repository_id text NOT NULL,
    project_id text NOT NULL,
    card_id text NOT NULL,
    attempt_id text NOT NULL,
    source_branch text NOT NULL,
    target_branch text NOT NULL,
    expected_source_sha text NULL,
    expected_base_sha text NULL,
    state text NOT NULL
        CHECK (state IN ('pending', 'merging', 'merged', 'failed', 'aborted')),
    owner_id text NULL,
    fencing_token bigint NOT NULL DEFAULT 0 CHECK (fencing_token >= 0),
    lease_expires_at timestamptz NULL,
    result_sha text NULL,
    board_settled boolean NOT NULL DEFAULT false,
    last_error text NULL CHECK (last_error IS NULL OR length(last_error) <= 4000),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, merge_intent_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects (tenant_id, id)
);

-- UM merge ativo por repositório, garantido pelo BANCO. Um semáforo em memória protegia um
-- processo; isto protege o repositório, que é o recurso de verdade disputado.
CREATE UNIQUE INDEX ux_merge_intents_active_repository
    ON harness.merge_intents (tenant_id, repository_id)
    WHERE state = 'merging';

-- Um card tem uma intenção por tentativa: retry reusa a mesma, não abre outra.
CREATE UNIQUE INDEX ux_merge_intents_attempt
    ON harness.merge_intents (tenant_id, attempt_id);

CREATE INDEX ix_merge_intents_unsettled
    ON harness.merge_intents (updated_at, tenant_id, merge_intent_id)
    WHERE state IN ('pending', 'merging') OR board_settled = false;

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.merge_intents ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.merge_intents FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.merge_intents
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
