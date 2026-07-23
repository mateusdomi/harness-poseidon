-- ARC-01: modelo arquitetural ESTRUTURADO (elementos + relacionamentos), nunca imagens. O modelo
-- PROPOSTO e o VIGENTE coexistem separados pela coluna 'state' ('implemented'/'proposed') — ARC-05.
-- 'locked' impede que uma mudança proposta por agente sobrescreva um elemento vigente (ARC-05).
-- O histórico é APPEND-ONLY: cada versão vira uma linha auditável (versionamento ARC-01 + rollback).
CREATE TABLE harness.architecture_elements
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    kind text NOT NULL CHECK (kind IN ('system','container','component','dataStore','actor','external')),
    name text NOT NULL,
    description text NOT NULL,
    properties_json jsonb NOT NULL,
    state text NOT NULL CHECK (state IN ('implemented','proposed')),
    locked boolean NOT NULL DEFAULT false,
    version integer NOT NULL DEFAULT 1,
    proposal_id char(26) NULL,
    counterpart_id char(26) NULL,
    change_kind text NULL CHECK (change_kind IS NULL OR change_kind IN ('add','modify','remove')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_elements_scope
    ON harness.architecture_elements (tenant_id,state,kind,id);
CREATE INDEX ix_architecture_elements_proposal
    ON harness.architecture_elements (tenant_id,proposal_id);

CREATE TABLE harness.architecture_relationships
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    project_id char(26) NULL,
    source_id char(26) NOT NULL,
    target_id char(26) NOT NULL,
    kind text NOT NULL CHECK (kind IN ('uses','depends-on','calls','contains','flows-to')),
    properties_json jsonb NOT NULL,
    state text NOT NULL CHECK (state IN ('implemented','proposed')),
    version integer NOT NULL DEFAULT 1,
    proposal_id char(26) NULL,
    counterpart_id char(26) NULL,
    change_kind text NULL CHECK (change_kind IS NULL OR change_kind IN ('add','modify','remove')),
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_relationships_scope
    ON harness.architecture_relationships (tenant_id,state,id);
CREATE INDEX ix_architecture_relationships_proposal
    ON harness.architecture_relationships (tenant_id,proposal_id);

CREATE TABLE harness.architecture_element_history
(
    id char(26) PRIMARY KEY,
    tenant_id char(26) NOT NULL,
    entity_type text NOT NULL CHECK (entity_type IN ('element','relationship')),
    entity_id char(26) NOT NULL,
    version integer NOT NULL,
    snapshot_json jsonb NOT NULL,
    change_kind text NOT NULL,
    actor text NULL,
    justification text NULL,
    occurred_at timestamptz NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_element_history_entity
    ON harness.architecture_element_history (tenant_id,entity_id,occurred_at,id);
