-- CHECKPOINT DE EXECUÇÃO — o estado recuperável pertence ao CARD, não à tentativa nem ao ator.
--
-- O modelo anterior amarrava o estado retomável à tentativa e ao ator que a produziu: a política
-- de continuação exigia o MESMO alias. Isso contradiz exatamente o caso que ela precisava cobrir —
-- quando a cota esgota no meio do trabalho, continuar significa trocar de conta. Resultado: o
-- trabalho parcial era descartado e a nova tentativa recomeçava do zero.
--
-- Aqui o checkpoint é do par (execução, card). A tentativa é descartável e fenced; conta, provider
-- e executor são recursos substituíveis desde que compatíveis.
CREATE TABLE execution_checkpoints
(
    tenant_id TEXT NOT NULL,
    checkpoint_id TEXT NOT NULL CHECK (length(checkpoint_id) = 26),
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    execution_id TEXT NOT NULL,
    -- Tentativa de ORIGEM: proveniência, não vínculo. A retomada não exige que ela volte.
    source_attempt_id TEXT NOT NULL,
    source_account_alias TEXT NOT NULL,
    source_role TEXT NOT NULL,
    -- Por que o checkpoint foi tirado. `quota` é o que autoriza a troca de conta na retomada;
    -- `review` mantém a exigência de mesmo ator, porque quem corrige o próprio achado é quem o fez.
    origin TEXT NOT NULL CHECK (origin IN ('quota','transient','cancelled','review')),
    branch_name TEXT NOT NULL,
    source_commit TEXT NULL,
    repository_root TEXT NOT NULL,
    scope_claims_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(scope_claims_json) AND json_type(scope_claims_json) = 'array'),
    changed_files_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(changed_files_json) AND json_type(changed_files_json) = 'array'),
    -- Resumo do que já foi feito e do que falta: é o que permite continuar em vez de refazer.
    progress_note TEXT NULL CHECK (progress_note IS NULL OR length(progress_note) <= 8000),
    pending_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(pending_json) AND json_type(pending_json) = 'array'),
    evidence_json TEXT NOT NULL DEFAULT '[]'
        CHECK (json_valid(evidence_json) AND json_type(evidence_json) = 'array'),
    -- Fencing da tentativa de origem. Um resultado tardio dela nunca publica sobre a nova.
    origin_fencing_token INTEGER NOT NULL DEFAULT 0,
    -- Tentativa que consumiu este checkpoint; nula enquanto ele estiver disponível.
    consumed_by_attempt_id TEXT NULL,
    consumed_at TEXT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, checkpoint_id)
);

-- Um checkpoint DISPONÍVEL por card: o mais recente é o que vale, e dois disponíveis ao mesmo
-- tempo deixariam a retomada ambígua.
CREATE UNIQUE INDEX ux_execution_checkpoints_available
    ON execution_checkpoints (tenant_id, task_id)
    WHERE consumed_by_attempt_id IS NULL;

CREATE INDEX ix_execution_checkpoints_task
    ON execution_checkpoints (tenant_id, task_id, created_at);
