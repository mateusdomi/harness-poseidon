-- Fase 0B2 (BR-002/BR-013): diário de CHAMADAS de ferramenta.
--
-- O PEP autorizava o BINÁRIO executor uma vez, no início da tentativa; nenhuma chamada posterior
-- voltava a passar por política. Autorizar o processo e não os efeitos é autorizar a intenção e não
-- o ato. Aqui cada chamada — permitida ou NEGADA — deixa registro com quem pediu, sobre o quê e com
-- que desfecho; as negativas são justamente as que interessam quando algo dá errado.
--
-- A chave de idempotência é a identidade da chamada: repetir a mesma chave devolve o resultado
-- anterior em vez de repetir o efeito. É o que separa "tentar de novo" de "fazer duas vezes".
CREATE TABLE harness.tool_call_journal
(
    tenant_id text NOT NULL,
    idempotency_key text NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    project_id text NOT NULL,
    card_id text NOT NULL,
    attempt_id text NOT NULL,
    agent_id text NOT NULL,
    profile text NOT NULL CHECK (profile IN ('Chief', 'Actor', 'Critic')),
    tool_id text NOT NULL,
    fencing_token bigint NOT NULL,
    paths_json jsonb NOT NULL,
    network_enabled boolean NOT NULL,
    mutating boolean NOT NULL,
    allowed boolean NOT NULL,
    code text NOT NULL,
    detail text NOT NULL CHECK (length(detail) <= 4000),
    output text NOT NULL,
    output_truncated boolean NOT NULL,
    exit_code integer NOT NULL,
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key)
);

CREATE INDEX ix_tool_call_journal_attempt
    ON harness.tool_call_journal (tenant_id, attempt_id, occurred_at);

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.tool_call_journal ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.tool_call_journal FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.tool_call_journal
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
