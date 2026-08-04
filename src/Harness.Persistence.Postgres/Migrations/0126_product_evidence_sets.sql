-- Migration 0126: ledger append-only dos conjuntos de evidência de produto.
-- Paridade com o SQLite; ver a migração equivalente para o racional completo.
CREATE TABLE harness.product_evidence_sets
(
    tenant_id text NOT NULL,
    evidence_set_id text NOT NULL,
    project_id text NOT NULL,
    workflow_run_id text NULL,
    task_id text NULL,
    attempt_id text NULL,
    commit_sha text NOT NULL,
    profile_version integer NOT NULL CHECK (profile_version >= 1),
    profile_fingerprint text NOT NULL,
    modality text NOT NULL,
    gate_decision text NOT NULL CHECK (gate_decision IN ('satisfied', 'failed')),
    plan_json jsonb NOT NULL,
    items_json jsonb NOT NULL,
    findings_json jsonb NOT NULL,
    collectors text NOT NULL,
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, evidence_set_id)
);

CREATE INDEX ix_product_evidence_sets_project
    ON harness.product_evidence_sets (tenant_id, project_id, created_at DESC);

CREATE INDEX ix_product_evidence_sets_commit
    ON harness.product_evidence_sets (tenant_id, project_id, commit_sha);

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.product_evidence_sets ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.product_evidence_sets FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.product_evidence_sets
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
