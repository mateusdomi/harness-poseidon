-- Fase 0B1 (BR-002/BR-013): a sandbox deixa de ser uma afirmação literal e passa a ser um FATO
-- registrado, por tentativa, com emissor identificado.
--
-- ANTES: o control plane informava `SandboxActive: true` fixo — inclusive com
-- `IsolatedExecution.Mode=Disabled`, ou seja, com sandbox nenhuma. A política de ferramentas lia
-- esse literal e liberava execução de risco crítico acreditando existir uma fronteira inexistente.
--
-- A attestation é POR TENTATIVA porque uma execução isolada no passado não contém a de agora.
CREATE TABLE sandbox_attestations
(
    tenant_id TEXT NOT NULL,
    attempt_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    provider TEXT NOT NULL CHECK (length(provider) BETWEEN 1 AND 100),
    provider_version TEXT NOT NULL,
    sandbox_identity TEXT NOT NULL,
    mounts_json TEXT NOT NULL CHECK (json_valid(mounts_json)),
    network_policy TEXT NOT NULL,
    root_filesystem_read_only INTEGER NOT NULL CHECK (root_filesystem_read_only IN (0, 1)),
    worktree_isolated INTEGER NOT NULL CHECK (worktree_isolated IN (0, 1)),
    egress_restricted INTEGER NOT NULL CHECK (egress_restricted IN (0, 1)),
    resource_limits_applied INTEGER NOT NULL CHECK (resource_limits_applied IN (0, 1)),
    verified INTEGER NOT NULL CHECK (verified IN (0, 1)),
    verification_detail TEXT NOT NULL CHECK (length(verification_detail) <= 4000),
    configuration_hash TEXT NOT NULL CHECK (length(configuration_hash) = 64),
    issued_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, attempt_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects (tenant_id, id)
);

CREATE INDEX ix_sandbox_attestations_project
    ON sandbox_attestations (tenant_id, project_id, issued_at);

-- O aceite do MODO INSEGURO é do proprietário e de mais ninguém. Ele é persistido, tem autor,
-- motivo e validade, e é auditado — porque "aceitar risco" sem registro é o mesmo que não ter
-- política. Nenhum agente escreve aqui: a única porta é a sessão de perfil local do dono.
CREATE TABLE unsafe_execution_acceptances
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    accepted_by_profile_id TEXT NOT NULL,
    reason TEXT NOT NULL CHECK (length(reason) BETWEEN 10 AND 2000),
    accepted_at TEXT NOT NULL,
    expires_at TEXT NOT NULL,
    revoked_at TEXT NULL,
    PRIMARY KEY (tenant_id, project_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects (tenant_id, id),
    FOREIGN KEY (tenant_id, accepted_by_profile_id) REFERENCES local_users (tenant_id, id)
);
