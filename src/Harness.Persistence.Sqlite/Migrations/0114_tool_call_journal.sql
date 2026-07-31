-- Fase 0B2 (BR-002/BR-013): diário de CHAMADAS de ferramenta.
--
-- O PEP autorizava o BINÁRIO executor uma vez, no início da tentativa; nenhuma chamada posterior
-- voltava a passar por política. Autorizar o processo e não os efeitos é autorizar a intenção e não
-- o ato. Aqui cada chamada — permitida ou NEGADA — deixa registro com quem pediu, sobre o quê e com
-- que desfecho; as negativas são justamente as que interessam quando algo dá errado.
--
-- A chave de idempotência é a identidade da chamada: repetir a mesma chave devolve o resultado
-- anterior em vez de repetir o efeito. É o que separa "tentar de novo" de "fazer duas vezes".
CREATE TABLE tool_call_journal
(
    tenant_id TEXT NOT NULL,
    idempotency_key TEXT NOT NULL CHECK (length(idempotency_key) BETWEEN 1 AND 200),
    project_id TEXT NOT NULL,
    card_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    agent_id TEXT NOT NULL,
    profile TEXT NOT NULL CHECK (profile IN ('Chief', 'Actor', 'Critic')),
    tool_id TEXT NOT NULL,
    fencing_token INTEGER NOT NULL,
    paths_json TEXT NOT NULL CHECK (json_valid(paths_json)),
    network_enabled INTEGER NOT NULL CHECK (network_enabled IN (0, 1)),
    mutating INTEGER NOT NULL CHECK (mutating IN (0, 1)),
    allowed INTEGER NOT NULL CHECK (allowed IN (0, 1)),
    code TEXT NOT NULL,
    detail TEXT NOT NULL CHECK (length(detail) <= 4000),
    output TEXT NOT NULL,
    output_truncated INTEGER NOT NULL CHECK (output_truncated IN (0, 1)),
    exit_code INTEGER NOT NULL,
    occurred_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, idempotency_key)
);

CREATE INDEX ix_tool_call_journal_attempt
    ON tool_call_journal (tenant_id, attempt_id, occurred_at);
