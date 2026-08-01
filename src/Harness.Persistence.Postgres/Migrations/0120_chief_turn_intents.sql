-- Migration 0120 (Fase 2B / B14): o turno da chefe passa a ser MEDIDO por intenção.
--
-- O turno é o laço mais quente e mais caro do produto — roda a cada mensagem do dono — e era o
-- único caminho de execução sem nenhum registro de custo ou duração. `model_invocations` cobre os
-- runs de especialista; o turno da chefe não escrevia lá nem em lugar nenhum. Na prática, o painel
-- de produtividade media tudo menos a peça que mais executa.
--
-- Sem esta tabela, o objetivo declarado do B14 — reduzir variância e custo do laço mais quente —
-- seria afirmação sem número. E a média de todos os turnos esconderia exatamente a intenção cara:
-- um "bom dia" e um "planeje o projeto inteiro" entrariam no mesmo balde.
--
-- `actions_dropped` registra quando a rota da intenção cortou ações que o modelo propôs. É o dado
-- que diz se a taxonomia está bem desenhada: corte frequente numa intenção significa ou modelo
-- classificando mal, ou rota apertada demais — e as duas hipóteses se distinguem olhando a série.

CREATE TABLE harness.chief_turn_intents
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    turn_id text NOT NULL,
    intent text NOT NULL CHECK (length(intent) BETWEEN 1 AND 60),
    confidence double precision NOT NULL CHECK (confidence >= 0 AND confidence <= 1),
    -- Quantas ações a rota descartou. Zero é o caso saudável.
    demands_dropped integer NOT NULL DEFAULT 0 CHECK (demands_dropped >= 0),
    team_actions_dropped integer NOT NULL DEFAULT 0 CHECK (team_actions_dropped >= 0),
    duration_ms bigint NOT NULL CHECK (duration_ms >= 0),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, turn_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

-- A consulta do painel é sempre "por projeto, do mais recente para o mais antigo".
CREATE INDEX ix_chief_turn_intents_project
    ON harness.chief_turn_intents (tenant_id, project_id, occurred_at DESC);

-- RLS obrigatória em toda tabela nova com tenant (desde a migração 0084): sem ela, uma consulta
-- que esqueça o filtro de tenant lê o turno de outro dono e nada acusa.
ALTER TABLE harness.chief_turn_intents ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.chief_turn_intents FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.chief_turn_intents
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
