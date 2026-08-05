-- Dual Project Gate: a PK de project_graph_nodes era (tenant_id, id), mas o id determinístico
-- ("humanfact:banco-oracle-19c") é único POR PROJETO — com dois projetos reais, o nó do segundo
-- colidia com o do primeiro e o upsert atualizava a linha do projeto ERRADO (observado ao vivo:
-- o HumanFact Oracle do Indicadores sumiu porque a linha pertencia ao Prisma). A chave passa a
-- incluir o projeto; as arestas seguem o mesmo escopo.
CREATE TABLE project_graph_nodes_new
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL,
    project_id TEXT NOT NULL CHECK (length(project_id) = 26),
    type TEXT NOT NULL CHECK (type IN (
        'requirement', 'nfr', 'human_fact', 'constraint', 'decision', 'risk', 'open_question',
        'artifact', 'phase', 'gate', 'card', 'test', 'evidence')),
    canonical_source_id TEXT NOT NULL,
    canonical_source_kind TEXT NOT NULL,
    version INTEGER NOT NULL CHECK (version >= 0),
    state TEXT NOT NULL CHECK (state IN ('active', 'stale', 'retired')),
    provenance TEXT NOT NULL CHECK (provenance IN ('deterministic', 'model_inference')),
    confidence REAL NOT NULL CHECK (confidence BETWEEN 0.0 AND 1.0),
    title TEXT NOT NULL,
    stale_cause_node_id TEXT NULL,
    stale_cause_version INTEGER NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, project_id, id),
    UNIQUE (tenant_id, project_id, type, canonical_source_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

INSERT INTO project_graph_nodes_new SELECT * FROM project_graph_nodes;

CREATE TABLE project_graph_edges_new
(
    tenant_id TEXT NOT NULL,
    id TEXT NOT NULL,
    project_id TEXT NOT NULL CHECK (length(project_id) = 26),
    from_node_id TEXT NOT NULL,
    to_node_id TEXT NOT NULL,
    relation_type TEXT NOT NULL CHECK (relation_type IN (
        'implements', 'verifies', 'proves', 'derives_from', 'constrained_by', 'impacts',
        'depends_on', 'blocked_by', 'supersedes', 'invalidates', 'requires', 'produces')),
    provenance TEXT NOT NULL CHECK (provenance IN ('deterministic', 'model_inference')),
    confidence REAL NOT NULL CHECK (confidence BETWEEN 0.0 AND 1.0),
    status TEXT NOT NULL CHECK (status IN ('proposed', 'accepted')),
    valid_from TEXT NOT NULL,
    valid_until TEXT NULL,
    CHECK (provenance != 'deterministic' OR (confidence = 1.0 AND status = 'accepted')),
    PRIMARY KEY (tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, from_node_id)
        REFERENCES project_graph_nodes_new(tenant_id, project_id, id),
    FOREIGN KEY (tenant_id, project_id, to_node_id)
        REFERENCES project_graph_nodes_new(tenant_id, project_id, id)
);

INSERT INTO project_graph_edges_new SELECT * FROM project_graph_edges;

DROP TABLE project_graph_edges;
DROP TABLE project_graph_nodes;
ALTER TABLE project_graph_nodes_new RENAME TO project_graph_nodes;
ALTER TABLE project_graph_edges_new RENAME TO project_graph_edges;

CREATE INDEX ix_project_graph_nodes_project ON project_graph_nodes (tenant_id, project_id, type);
CREATE INDEX ix_project_graph_edges_project ON project_graph_edges (tenant_id, project_id);
CREATE INDEX ix_project_graph_edges_from ON project_graph_edges (tenant_id, from_node_id);
CREATE INDEX ix_project_graph_edges_to ON project_graph_edges (tenant_id, to_node_id);
