-- Migration 0123: adiciona a causa tipada da reprovação em work_reviews.
-- F-22 — classificar a rejeição permite métricas e direcionamento da correção
-- sem depender de parsing do campo rationale.
ALTER TABLE harness.work_reviews
    ADD COLUMN rejection_cause varchar(30) NOT NULL DEFAULT 'none'
    CHECK (rejection_cause IN ('none', 'contextMissing', 'acceptanceNotMet', 'scopeViolation', 'qualityBar', 'other'));
