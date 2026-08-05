-- Migration 0127: papel tipado do anexo — a proveniência do artefato fornecido.
--
-- O anexo era genérico: um ZIP com o frontend do usuário e um PDF de apoio eram a mesma coisa
-- para o runtime. O papel é o que permite a um card de frontend referenciar "o frontend que o
-- usuário FORNECEU e deve ser evoluído" — sem papel, o executor não tem como saber que o artefato
-- existe com essa autoridade, e reconstruir do zero vira o caminho de menor resistência.
--
-- Conjunto fechado; `other` é o default de compatibilidade para tudo que já existia.
ALTER TABLE solicitation_attachments ADD COLUMN role TEXT NOT NULL DEFAULT 'other'
    CHECK (role IN ('requirements_source', 'provided_frontend', 'design_reference', 'supporting_document', 'other'));
