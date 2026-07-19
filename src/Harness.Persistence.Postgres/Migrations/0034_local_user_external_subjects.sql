-- OIDC: vínculo do perfil local ao subject do provedor de identidade externo.
ALTER TABLE harness.local_users ADD COLUMN external_subject text NULL CHECK (external_subject IS NULL OR length(external_subject) BETWEEN 1 AND 200);
CREATE UNIQUE INDEX ux_local_users_external_subject ON harness.local_users (external_subject) WHERE external_subject IS NOT NULL;
