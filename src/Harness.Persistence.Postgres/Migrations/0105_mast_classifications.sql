-- Paridade com o SQLite: classificação MAST da tentativa (B1/F16). Uma tentativa tem UM modo —
-- reclassificar substitui, não acumula, senão a distribuição contaria a mesma falha várias vezes.
CREATE TABLE harness.mast_attempt_classifications
(
    tenant_id text NOT NULL,
    attempt_id text NOT NULL,
    project_id text NOT NULL,
    task_id text NOT NULL,
    failure_mode_code text NOT NULL CHECK (length(failure_mode_code) BETWEEN 1 AND 100),
    category text NOT NULL CHECK (category IN ('specification', 'misalignment', 'verification')),
    classified_by text NOT NULL CHECK (length(classified_by) BETWEEN 1 AND 100),
    evidence text NULL CHECK (evidence IS NULL OR length(evidence) <= 4000),
    occurred_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, attempt_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects(tenant_id, id)
);

CREATE INDEX ix_mast_attempt_classifications_project
    ON harness.mast_attempt_classifications (tenant_id, project_id, occurred_at);
CREATE INDEX ix_mast_attempt_classifications_task
    ON harness.mast_attempt_classifications (tenant_id, task_id, occurred_at);

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.mast_attempt_classifications ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.mast_attempt_classifications FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.mast_attempt_classifications
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
