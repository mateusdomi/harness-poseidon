-- Paridade com o SQLite: attestation de sandbox por tentativa e aceite de modo inseguro do
-- proprietário (Fase 0B1, BR-002/BR-013). Mesma semântica, mesmos nomes lógicos.
CREATE TABLE harness.sandbox_attestations
(
    tenant_id text NOT NULL,
    attempt_id text NOT NULL,
    project_id text NOT NULL,
    provider text NOT NULL CHECK (length(provider) BETWEEN 1 AND 100),
    provider_version text NOT NULL,
    sandbox_identity text NOT NULL,
    mounts_json jsonb NOT NULL,
    network_policy text NOT NULL,
    root_filesystem_read_only boolean NOT NULL,
    worktree_isolated boolean NOT NULL,
    egress_restricted boolean NOT NULL,
    resource_limits_applied boolean NOT NULL,
    verified boolean NOT NULL,
    verification_detail text NOT NULL CHECK (length(verification_detail) <= 4000),
    configuration_hash text NOT NULL CHECK (length(configuration_hash) = 64),
    issued_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, attempt_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects (tenant_id, id)
);

CREATE INDEX ix_sandbox_attestations_project
    ON harness.sandbox_attestations (tenant_id, project_id, issued_at);

CREATE TABLE harness.unsafe_execution_acceptances
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    accepted_by_profile_id text NOT NULL,
    reason text NOT NULL CHECK (length(reason) BETWEEN 10 AND 2000),
    accepted_at timestamptz NOT NULL,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz NULL,
    PRIMARY KEY (tenant_id, project_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES harness.projects (tenant_id, id)
);

-- Isolamento por tenant é obrigatório em toda tabela nova (0084 forçou a regra no schema inteiro).
ALTER TABLE harness.sandbox_attestations ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.sandbox_attestations FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.sandbox_attestations
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));

ALTER TABLE harness.unsafe_execution_acceptances ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.unsafe_execution_acceptances FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.unsafe_execution_acceptances
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
