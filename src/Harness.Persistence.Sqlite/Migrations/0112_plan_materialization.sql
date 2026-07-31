-- Fase 0A1 (BR-001/BR-004): materialização durável e idempotente do caminho demanda → plano → cards.
--
-- ANTES: o turno do Chefe commitava a resposta e as demandas e SÓ DEPOIS, em memória e dentro de um
-- try/catch, gerava o plano e criava os cards. Uma queda entre as duas coisas deixava a demanda sem
-- plano para sempre (BR-004), e o marker `materialized_at` era gravado ANTES dos cards, de modo que
-- uma queda no meio produzia um plano permanentemente marcado com cards faltando (BR-001).
--
-- 1. `demand_materializations` é o COMPROMISSO durável, gravado na MESMA transação que conclui o
--    turno e persiste as demandas. Carrega a intenção completa do turno (critérios de aceite,
--    especialidade e superfícies declaradas — o que só existia na memória do worker) e o estado
--    factual do planejamento: `pending`, `processing`, `completed`, `failed`. Enquanto ele não
--    estiver `completed`, o planejamento NÃO está concluído — e isso é visível, não presumido.
-- 2. `work_tasks.plan_id`/`plan_slice_key` dão ao card a chave lógica do plano que o originou. O
--    índice único parcial torna "retry não duplica card" uma invariante do BANCO, e não uma
--    promessa do código de aplicação: dois consumidores concorrentes convergem para o mesmo card.
--
-- Compatibilidade: as colunas são NULL para todo card já existente e o índice é parcial, portanto
-- nenhum dado histórico é reescrito por esta migration. Cards anteriores ao novo fluxo são
-- ADOTADOS sob demanda pelo próprio materializador (carimbo por código do plano), nunca em massa.

ALTER TABLE work_tasks ADD COLUMN plan_id TEXT NULL;
ALTER TABLE work_tasks ADD COLUMN plan_slice_key TEXT NULL;

CREATE UNIQUE INDEX ux_work_tasks_plan_slice
    ON work_tasks (tenant_id, plan_id, plan_slice_key)
    WHERE plan_id IS NOT NULL AND plan_slice_key IS NOT NULL;

CREATE TABLE demand_materializations
(
    tenant_id TEXT NOT NULL,
    demand_id TEXT NOT NULL CHECK (length(demand_id) = 26),
    project_id TEXT NOT NULL,
    turn_id TEXT NULL,
    plan_id TEXT NULL,
    status TEXT NOT NULL
        CHECK (status IN ('pending', 'processing', 'completed', 'failed')),
    attempt_count INTEGER NOT NULL DEFAULT 0 CHECK (attempt_count >= 0),
    expected_cards INTEGER NULL CHECK (expected_cards IS NULL OR expected_cards >= 0),
    materialized_cards INTEGER NULL CHECK (materialized_cards IS NULL OR materialized_cards >= 0),
    request_json TEXT NOT NULL CHECK (json_valid(request_json)),
    owner_id TEXT NULL,
    last_error TEXT NULL CHECK (last_error IS NULL OR length(last_error) <= 4000),
    requested_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    completed_at TEXT NULL,
    PRIMARY KEY (tenant_id, demand_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects (tenant_id, id),
    -- O compromisso nasce na mesma transação que insere a demanda; um registro apontando para uma
    -- demanda inexistente seria trabalho prometido a ninguém.
    FOREIGN KEY (tenant_id, project_id, demand_id)
        REFERENCES demands (tenant_id, project_id, id)
);

-- O reconciliador varre exatamente o que ainda não convergiu.
CREATE INDEX ix_demand_materializations_open
    ON demand_materializations (updated_at, tenant_id, demand_id)
    WHERE status <> 'completed';
