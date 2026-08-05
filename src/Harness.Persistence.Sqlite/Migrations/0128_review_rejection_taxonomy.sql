-- Migration 0128: taxonomia FECHADA e obrigatória da causa de rejeição (Onda 0.9).
--
-- A causa tipada existia (0123) com seis valores e sem obrigatoriedade: 96 dos 98 reviews
-- reprovados do histórico estavam com 'none' — a maior categoria de falha da fábrica era anônima,
-- e é exatamente o dado que alimenta o grafo de impacto, o analytics de assinaturas e o
-- aprendizado do harness. A taxonomia cresce para cobrir as causas REAIS observadas na perícia
-- (habilidade ausente, decomposição ruim, verificador/ferramenta ausente, limite do modelo,
-- ambiguidade de especificação, falha de ambiente, violação de política), e o histórico reprovado
-- sem causa vira 'unclassified' EXPLÍCITO — ausência declarada, nunca ausência silenciosa.
--
-- SQLite não altera CHECK: a tabela é reconstruída (o runner roda com foreign_keys OFF).
CREATE TABLE work_reviews_new
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    reviewer_agent_id TEXT NOT NULL CHECK (length(reviewer_agent_id) BETWEEN 1 AND 200),
    decision TEXT NOT NULL CHECK (decision IN ('approved', 'rejected')),
    rationale TEXT NOT NULL CHECK (length(rationale) BETWEEN 1 AND 10000),
    created_at TEXT NOT NULL,
    rejection_cause TEXT NOT NULL DEFAULT 'none'
        CHECK (rejection_cause IN (
            'none', 'unclassified',
            'contextMissing', 'missingSkill', 'badDecomposition', 'missingVerifier',
            'missingTool', 'modelCapability', 'specAmbiguity', 'environmentFailure',
            'policyViolation',
            'acceptanceNotMet', 'scopeViolation', 'qualityBar', 'other')),
    UNIQUE (attempt_id),
    FOREIGN KEY (tenant_id, project_id, attempt_id)
        REFERENCES work_attempts(tenant_id, project_id, id)
);
INSERT INTO work_reviews_new
    SELECT id, tenant_id, project_id, attempt_id, reviewer_agent_id, decision, rationale,
           created_at,
           CASE WHEN decision = 'rejected' AND rejection_cause = 'none'
                THEN 'unclassified' ELSE rejection_cause END
    FROM work_reviews;
DROP TABLE work_reviews;
ALTER TABLE work_reviews_new RENAME TO work_reviews;
