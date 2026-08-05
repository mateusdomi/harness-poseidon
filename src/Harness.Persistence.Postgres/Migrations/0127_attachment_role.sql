-- Migration 0127: papel tipado do anexo — a proveniência do artefato fornecido.
-- Ver a migration SQLite homônima para o racional completo.
ALTER TABLE harness.solicitation_attachments ADD COLUMN role TEXT NOT NULL DEFAULT 'other'
    CHECK (role IN ('requirements_source', 'provided_frontend', 'design_reference', 'supporting_document', 'other'));
