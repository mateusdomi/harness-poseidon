-- CLASSIFICAÇÃO MAST DAS TENTATIVAS (B1/F16) — o encerramento de uma tentativa produzia um veredito
-- binário: passou ou falhou. Isso basta para decidir retentativa e não serve para mais nada. Duas
-- tentativas que "falharam" podem ter falhado por motivos opostos — uma porque o enunciado estava
-- ambíguo, outra porque o agente terminou antes de conferir — e a correção de uma é o contrário da
-- correção da outra. O modo é o que transforma histórico em decisão.
CREATE TABLE mast_attempt_classifications
(
    tenant_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    task_id TEXT NOT NULL,
    -- Código do modo na taxonomia (ex.: 'mast.1_1_disobey_task_specification').
    failure_mode_code TEXT NOT NULL CHECK (length(failure_mode_code) BETWEEN 1 AND 100),
    -- Categoria derivada do modo: 'specification' | 'misalignment' | 'verification'.
    category TEXT NOT NULL CHECK (category IN ('specification', 'misalignment', 'verification')),
    -- Quem classificou e sobre que evidência: sem isso a distribuição vira número sem procedência.
    classified_by TEXT NOT NULL CHECK (length(classified_by) BETWEEN 1 AND 100),
    evidence TEXT NULL CHECK (evidence IS NULL OR length(evidence) <= 4000),
    occurred_at TEXT NOT NULL,
    -- Uma tentativa tem UM modo: reclassificar substitui, não acumula, senão a distribuição
    -- contaria a mesma falha várias vezes e o painel mentiria sobre a concentração.
    PRIMARY KEY (tenant_id, attempt_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

-- A leitura é sempre "distribuição do projeto" (painel) ou "da tarefa" (decisão da Bruna).
CREATE INDEX ix_mast_attempt_classifications_project
    ON mast_attempt_classifications (tenant_id, project_id, occurred_at);
CREATE INDEX ix_mast_attempt_classifications_task
    ON mast_attempt_classifications (tenant_id, task_id, occurred_at);
