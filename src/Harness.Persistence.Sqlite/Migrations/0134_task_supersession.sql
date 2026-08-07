-- Migration 0134: card Escalated pode ser SUPERSEDIDO por cards menores, sem buraco de cobertura.
--
-- Duas lacunas, achadas juntas na recuperação de throughput da Fase 5 (2026-08-07):
--
-- 1. WorkflowPhaseDriver.cs já compara `demand.State == "superseded"` (RequirementCoverageAnalyzer,
--    desde a lição do run de 2026-08-04: card cancelado sem substituto some com o requisito e
--    ninguém percebe). Mas o CHECK de `demands.state` só admitia
--    ('open','inProgress','completed','cancelled') — 'superseded' nunca era alcançável. Mesma
--    família de defeito do achado desta sessão em `work_attempts` (comparar contra um valor que o
--    CHECK não permite). SQLite não altera CHECK: a tabela é reconstruída (o runner roda com
--    foreign_keys OFF).
--
-- 2. Não havia registro auditável de QUEM substituiu QUEM quando um card escalado é encerrado —
--    só o cancelamento de um card `running` tinha caminho oficial (`CancelRunningTaskAsync`).
--    `task_supersessions` é o registro append-only mínimo: um card original, os substitutos que
--    assumiram o trabalho, o motivo e quem decidiu.
CREATE TABLE demands_new
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    solicitation_id TEXT NOT NULL,
    title TEXT NOT NULL CHECK (length(title) BETWEEN 1 AND 500),
    acceptance_criteria_json TEXT NOT NULL
        CHECK (json_valid(acceptance_criteria_json) AND json_type(acceptance_criteria_json) = 'array'),
    created_at TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', state TEXT NOT NULL DEFAULT 'open'
    CHECK (state IN ('open','inProgress','completed','cancelled','superseded')), priority TEXT NOT NULL DEFAULT 'medium'
    CHECK (priority IN ('low','medium','high','critical')), source_solicitation_id TEXT NULL REFERENCES solicitations(id), is_internal INTEGER NOT NULL DEFAULT 0
    CHECK (is_internal IN (0,1)), phase_name TEXT NULL CHECK (phase_name IS NULL OR length(phase_name) BETWEEN 1 AND 200),
    UNIQUE (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, solicitation_id)
        REFERENCES solicitations(tenant_id, project_id, id)
);
INSERT INTO demands_new SELECT * FROM demands;
DROP TABLE demands;
ALTER TABLE demands_new RENAME TO demands;
CREATE INDEX ix_demands_solicitation ON demands (solicitation_id, created_at, id);
CREATE INDEX ix_demands_board_project
    ON demands (tenant_id,project_id,id) WHERE is_internal=0;
CREATE INDEX ix_demands_project_phase ON demands (tenant_id,project_id,phase_name,id);

CREATE TABLE task_supersessions
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    original_task_id TEXT NOT NULL,
    replacement_task_ids_json TEXT NOT NULL
        CHECK (json_valid(replacement_task_ids_json) AND json_type(replacement_task_ids_json) = 'array'),
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 1 AND 10000),
    actor_kind TEXT NOT NULL CHECK (length(actor_kind) BETWEEN 1 AND 50),
    actor_id TEXT NOT NULL CHECK (length(actor_id) BETWEEN 1 AND 200),
    demand_superseded INTEGER NOT NULL DEFAULT 0 CHECK (demand_superseded IN (0,1)),
    occurred_at TEXT NOT NULL,
    UNIQUE (tenant_id, original_task_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);
CREATE INDEX ix_task_supersessions_project
    ON task_supersessions (tenant_id, project_id, occurred_at);
