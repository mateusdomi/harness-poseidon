-- CIRCUITO POR CARD — quando vários agentes falham no MESMO card, o problema é do card.
--
-- Já existe circuito por CONTA (CapacityManager): ele responde por provedor fora do ar, cota
-- estourada, autenticação vencida — indisponibilidade do recurso. Não cobre o caso oposto, e mais
-- caro: um card cujo enunciado é ambíguo, cujo escopo é impossível ou cujo critério de aceite não
-- pode ser atingido. Aí trocar de conta não muda nada, e cada nova rodada só repete o fracasso
-- gastando cota real.
--
-- Por isso a reabertura aqui NÃO é por cooldown. Tempo não corrige enunciado: o circuito aberto só
-- fecha quando a Bruna REPLANEJA o card (reescreve a instrução, corta o escopo ou o decompõe), e o
-- replanejamento fica registrado em replanned_at/replan_note para a auditoria distinguir
-- "consertado" de "esquecido".
CREATE TABLE card_circuit_breakers
(
    tenant_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    state TEXT NOT NULL DEFAULT 'closed' CHECK (state IN ('closed','open')),
    consecutive_failures INTEGER NOT NULL DEFAULT 0 CHECK (consecutive_failures >= 0),
    last_failure_reason_code TEXT NULL
        CHECK (last_failure_reason_code IS NULL OR length(last_failure_reason_code) <= 200),
    last_failure_at TEXT NULL,
    opened_at TEXT NULL,
    -- Proveniência do fechamento: sem isso, um circuito fechado não se distingue de um que nunca
    -- abriu, e a reincidência do mesmo card deixa de ser visível.
    replanned_at TEXT NULL,
    replan_note TEXT NULL CHECK (replan_note IS NULL OR length(replan_note) <= 2000),
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, task_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id),
    -- Aberto exige a marca de quando abriu: circuito aberto sem data não é auditável.
    CHECK ((state = 'open') = (opened_at IS NOT NULL))
);

-- A varredura que importa é "o que está aberto esperando replanejamento".
CREATE INDEX ix_card_circuit_breakers_open
    ON card_circuit_breakers (tenant_id, project_id, opened_at)
    WHERE state = 'open';
