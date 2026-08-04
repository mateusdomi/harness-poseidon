-- Migration 0126: ledger append-only dos conjuntos de evidência de produto.
--
-- O gate calculava a evidência, decidia e esquecia. A pergunta "exatamente quais evidências
-- fizeram este projeto passar?" não tinha resposta meses depois — e a tentativa 17 que reprovou
-- desaparecia quando a 18 passava, apagando justamente o que o Poseidon precisa para aprender.
--
-- Append-only por construção: cada avaliação cria uma linha nova, amarrada ao COMMIT verificado e
-- à versão do perfil vigente. Um conjunto nunca é atualizado para fingir que uma tentativa
-- anterior foi melhor do que foi.
CREATE TABLE product_evidence_sets
(
    tenant_id TEXT NOT NULL,
    evidence_set_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    workflow_run_id TEXT NULL,
    task_id TEXT NULL,
    attempt_id TEXT NULL,
    commit_sha TEXT NOT NULL,
    profile_version INTEGER NOT NULL CHECK (profile_version >= 1),
    profile_fingerprint TEXT NOT NULL,
    modality TEXT NOT NULL,
    gate_decision TEXT NOT NULL CHECK (gate_decision IN ('satisfied', 'failed')),
    plan_json TEXT NOT NULL CHECK (json_valid(plan_json)),
    items_json TEXT NOT NULL CHECK (json_valid(items_json)),
    findings_json TEXT NOT NULL CHECK (json_valid(findings_json)),
    collectors TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, evidence_set_id)
);

CREATE INDEX ix_product_evidence_sets_project
    ON product_evidence_sets (tenant_id, project_id, created_at DESC);

CREATE INDEX ix_product_evidence_sets_commit
    ON product_evidence_sets (tenant_id, project_id, commit_sha);
