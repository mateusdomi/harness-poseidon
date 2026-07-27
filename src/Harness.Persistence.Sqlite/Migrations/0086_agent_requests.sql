-- SOLICITAÇÃO ESTRUTURADA DE UM AGENTE À CHEFE.
--
-- Até aqui, um executor que precisava de informação só tinha um caminho: falhar, ser reprovado e
-- escalar depois de N ciclos. Caro, impreciso e semanticamente errado — a pergunta não é um
-- defeito do trabalho. A prosa dele também não chegava a lugar nenhum: a colheita só lê o id da
-- tentativa e a branch, então a dúvida morria no run.
--
-- A expansão de escopo é um tipo desta mesma solicitação: o agente descobre no meio do trabalho
-- que precisa de um path que o claim não cobre, e isso passa a ser um pedido auditável em vez de
-- uma ampliação silenciosa (ou de uma falha).
CREATE TABLE agent_requests
(
    tenant_id TEXT NOT NULL,
    request_id TEXT NOT NULL CHECK (length(request_id) = 26),
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    attempt_id TEXT NULL,
    kind TEXT NOT NULL CHECK (kind IN
        ('needs_decision','needs_clarification','blocked_by_dependency',
         'scope_expansion','external_resource','canonical_conflict')),
    question TEXT NOT NULL CHECK (length(question) BETWEEN 1 AND 4000),
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 4000),
    options_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(options_json) AND json_type(options_json) = 'array'),
    recommended_option TEXT NULL,
    evidence_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(evidence_json) AND json_type(evidence_json) = 'array'),
    -- Paths pedidos quando `kind='scope_expansion'`; vazio nos demais tipos.
    requested_paths_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(requested_paths_json) AND json_type(requested_paths_json) = 'array'),
    blocking INTEGER NOT NULL DEFAULT 1 CHECK (blocking IN (0,1)),
    state TEXT NOT NULL DEFAULT 'open' CHECK (state IN
        ('open','answered','rejected','escalated','superseded')),
    -- Quem respondeu: a chefe (decisão operacional) ou o humano (escalonamento legítimo).
    answered_by TEXT NULL CHECK (answered_by IS NULL OR answered_by IN ('chief','human')),
    answer TEXT NULL CHECK (answer IS NULL OR length(answer) <= 8000),
    answer_reason_code TEXT NULL,
    -- Fencing da tentativa que PERGUNTOU. Uma resposta destinada a esta tentativa não pode ser
    -- consumida por outra que a substituiu depois de uma troca de conta ou recuperação.
    fencing_token INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    answered_at TEXT NULL,
    PRIMARY KEY (tenant_id, request_id)
);

CREATE INDEX ix_agent_requests_open
    ON agent_requests (tenant_id, project_id, state, created_at);

CREATE INDEX ix_agent_requests_task
    ON agent_requests (tenant_id, task_id, state);

-- Idempotência da PERGUNTA: o mesmo agente, na mesma tentativa, perguntando a mesma coisa depois
-- de um reinício não pode abrir uma segunda solicitação. Sem isto, cada restart do Host duplicaria
-- a fila da chefe com perguntas idênticas.
CREATE UNIQUE INDEX ux_agent_requests_open_per_attempt
    ON agent_requests (tenant_id, task_id, attempt_id, kind, question)
    WHERE state = 'open';
