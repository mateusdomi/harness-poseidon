-- Onda 1 — ProjectGraphProjection (Graph-Driven Project Intelligence).
--
-- O grafo é PROJEÇÃO derivada das fontes canônicas — nunca fonte da verdade: pode ser apagado e
-- reconstruído a qualquer momento (`graph rebuild`), e a reconstrução tem de ser idêntica ao
-- incremental. Tipos de nó e de aresta são conjuntos FECHADOS no CHECK: aresta genérica
-- ("relates_to") não existe nem por acidente de escrita — o banco a recusa.
CREATE TABLE project_graph_nodes
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
    -- A causa do STALE: qual nó, em qual versão, invalidou a premissa deste. STALE sem causa é
    -- ruído; com causa é trabalho de revalidação enunciável.
    stale_cause_node_id TEXT NULL,
    stale_cause_version INTEGER NULL,
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, project_id, type, canonical_source_id),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

CREATE INDEX ix_project_graph_nodes_project ON project_graph_nodes (tenant_id, project_id, type);

CREATE TABLE project_graph_edges
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
    -- Invariantes da Onda 1 no próprio banco: estrutural é 1.0/accepted; inferida nasce proposed.
    CHECK (provenance != 'deterministic' OR (confidence = 1.0 AND status = 'accepted')),
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, from_node_id) REFERENCES project_graph_nodes(tenant_id, id),
    FOREIGN KEY (tenant_id, to_node_id) REFERENCES project_graph_nodes(tenant_id, id)
);

CREATE INDEX ix_project_graph_edges_project ON project_graph_edges (tenant_id, project_id);
CREATE INDEX ix_project_graph_edges_from ON project_graph_edges (tenant_id, from_node_id);
CREATE INDEX ix_project_graph_edges_to ON project_graph_edges (tenant_id, to_node_id);

-- A versão monotônica da projeção por projeto: cada aplicação (incremental ou rebuild) grava
-- uma linha com o motivo. É o que torna "que grafo a decisão viu?" uma pergunta respondível.
CREATE TABLE project_graph_snapshots
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL CHECK (length(project_id) = 26),
    graph_version INTEGER NOT NULL CHECK (graph_version > 0),
    reason TEXT NOT NULL,
    node_count INTEGER NOT NULL CHECK (node_count >= 0),
    edge_count INTEGER NOT NULL CHECK (edge_count >= 0),
    created_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, project_id, graph_version),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);
