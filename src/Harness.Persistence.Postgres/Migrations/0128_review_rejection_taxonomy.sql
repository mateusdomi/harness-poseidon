-- Migration 0128: taxonomia fechada e obrigatória da causa de rejeição (Onda 0.9).
-- Ver a migration SQLite homônima para o racional completo.
ALTER TABLE harness.work_reviews DROP CONSTRAINT IF EXISTS work_reviews_rejection_cause_check;
UPDATE harness.work_reviews SET rejection_cause = 'unclassified'
    WHERE decision = 'rejected' AND rejection_cause = 'none';
ALTER TABLE harness.work_reviews ADD CONSTRAINT work_reviews_rejection_cause_check
    CHECK (rejection_cause IN (
        'none', 'unclassified',
        'contextMissing', 'missingSkill', 'badDecomposition', 'missingVerifier',
        'missingTool', 'modelCapability', 'specAmbiguity', 'environmentFailure',
        'policyViolation',
        'acceptanceNotMet', 'scopeViolation', 'qualityBar', 'other'));
