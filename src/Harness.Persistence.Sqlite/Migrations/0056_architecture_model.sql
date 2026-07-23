-- ARC-01: modelo arquitetural ESTRUTURADO (elementos + relacionamentos), nunca imagens. O modelo
-- PROPOSTO e o VIGENTE coexistem separados pela coluna 'state' ('implemented'/'proposed') — ARC-05.
-- 'locked' impede que uma mudança proposta por agente sobrescreva um elemento vigente (ARC-05).
-- O histórico é APPEND-ONLY: cada versão de um elemento/relacionamento vira uma linha auditável,
-- permitindo rollback e o versionamento exigido em ARC-01. Nada é inventado aqui: apenas o que foi
-- gravado pelo arquiteto ou proposto por um agente.
CREATE TABLE architecture_elements
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('system','container','component','dataStore','actor','external')),
    name TEXT NOT NULL,
    description TEXT NOT NULL,
    properties_json TEXT NOT NULL CHECK (json_valid(properties_json)),
    state TEXT NOT NULL CHECK (state IN ('implemented','proposed')),
    locked INTEGER NOT NULL DEFAULT 0 CHECK (locked IN (0,1)),
    version INTEGER NOT NULL DEFAULT 1,
    proposal_id TEXT NULL,
    counterpart_id TEXT NULL,
    change_kind TEXT NULL CHECK (change_kind IS NULL OR change_kind IN ('add','modify','remove')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_elements_scope
    ON architecture_elements (tenant_id,state,kind,id);
CREATE INDEX ix_architecture_elements_proposal
    ON architecture_elements (tenant_id,proposal_id);

CREATE TABLE architecture_relationships
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    project_id TEXT NULL,
    source_id TEXT NOT NULL,
    target_id TEXT NOT NULL,
    kind TEXT NOT NULL CHECK (kind IN ('uses','depends-on','calls','contains','flows-to')),
    properties_json TEXT NOT NULL CHECK (json_valid(properties_json)),
    state TEXT NOT NULL CHECK (state IN ('implemented','proposed')),
    version INTEGER NOT NULL DEFAULT 1,
    proposal_id TEXT NULL,
    counterpart_id TEXT NULL,
    change_kind TEXT NULL CHECK (change_kind IS NULL OR change_kind IN ('add','modify','remove')),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_relationships_scope
    ON architecture_relationships (tenant_id,state,id);
CREATE INDEX ix_architecture_relationships_proposal
    ON architecture_relationships (tenant_id,proposal_id);

CREATE TABLE architecture_element_history
(
    id TEXT PRIMARY KEY CHECK (length(id) = 26),
    tenant_id TEXT NOT NULL,
    entity_type TEXT NOT NULL CHECK (entity_type IN ('element','relationship')),
    entity_id TEXT NOT NULL,
    version INTEGER NOT NULL,
    snapshot_json TEXT NOT NULL CHECK (json_valid(snapshot_json)),
    change_kind TEXT NOT NULL,
    actor TEXT NULL,
    justification TEXT NULL,
    occurred_at TEXT NOT NULL,
    UNIQUE (tenant_id,id)
);

CREATE INDEX ix_architecture_element_history_entity
    ON architecture_element_history (tenant_id,entity_id,occurred_at,id);
