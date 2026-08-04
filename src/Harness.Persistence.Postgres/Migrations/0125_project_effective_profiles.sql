-- Migration 0125: histórico append-only do perfil efetivo do projeto.
-- Paridade com o SQLite; ver a migração equivalente para o racional completo.
CREATE TABLE harness.project_effective_profiles
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    version integer NOT NULL CHECK (version >= 1),
    fingerprint text NOT NULL,
    baseline_version text NOT NULL,
    modality text NOT NULL,
    profile_json jsonb NOT NULL,
    resolved_at timestamptz NOT NULL,
    resolved_by text NOT NULL,
    status text NOT NULL CHECK (status IN ('active', 'superseded')),
    PRIMARY KEY (tenant_id, project_id, version)
);

CREATE INDEX ix_project_effective_profiles_current
    ON harness.project_effective_profiles (tenant_id, project_id, version DESC);

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.project_effective_profiles ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.project_effective_profiles FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.project_effective_profiles
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
