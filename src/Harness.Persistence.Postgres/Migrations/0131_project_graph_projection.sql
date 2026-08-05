-- Migration 0131: ProjectGraphProjection (Onda 1). Ver a migration SQLite homônima para o
-- racional completo — projeção derivada, conjuntos fechados, invariantes no CHECK.
CREATE TABLE harness.project_graph_nodes
(
    tenant_id char(26) NOT NULL,
    id text NOT NULL,
    project_id char(26) NOT NULL,
    type varchar(20) NOT NULL CHECK (type IN (
        'requirement', 'nfr', 'human_fact', 'constraint', 'decision', 'risk', 'open_question',
        'artifact', 'phase', 'gate', 'card', 'test', 'evidence')),
    canonical_source_id text NOT NULL,
    canonical_source_kind text NOT NULL,
    version integer NOT NULL CHECK (version >= 0),
    state varchar(10) NOT NULL CHECK (state IN ('active', 'stale', 'retired')),
    provenance varchar(20) NOT NULL CHECK (provenance IN ('deterministic', 'model_inference')),
    confidence double precision NOT NULL CHECK (confidence BETWEEN 0.0 AND 1.0),
    title text NOT NULL,
    stale_cause_node_id text NULL,
    stale_cause_version integer NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, id),
    UNIQUE (tenant_id, project_id, type, canonical_source_id)
);
CREATE INDEX ix_project_graph_nodes_project ON harness.project_graph_nodes (tenant_id, project_id, type);
ALTER TABLE harness.project_graph_nodes ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.project_graph_nodes FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.project_graph_nodes
    USING (tenant_id = current_setting('app.tenant_id', true));

CREATE TABLE harness.project_graph_edges
(
    tenant_id char(26) NOT NULL,
    id text NOT NULL,
    project_id char(26) NOT NULL,
    from_node_id text NOT NULL,
    to_node_id text NOT NULL,
    relation_type varchar(20) NOT NULL CHECK (relation_type IN (
        'implements', 'verifies', 'proves', 'derives_from', 'constrained_by', 'impacts',
        'depends_on', 'blocked_by', 'supersedes', 'invalidates', 'requires', 'produces')),
    provenance varchar(20) NOT NULL CHECK (provenance IN ('deterministic', 'model_inference')),
    confidence double precision NOT NULL CHECK (confidence BETWEEN 0.0 AND 1.0),
    status varchar(10) NOT NULL CHECK (status IN ('proposed', 'accepted')),
    valid_from timestamptz NOT NULL,
    valid_until timestamptz NULL,
    CHECK (provenance != 'deterministic' OR (confidence = 1.0 AND status = 'accepted')),
    PRIMARY KEY (tenant_id, id),
    FOREIGN KEY (tenant_id, from_node_id) REFERENCES harness.project_graph_nodes(tenant_id, id),
    FOREIGN KEY (tenant_id, to_node_id) REFERENCES harness.project_graph_nodes(tenant_id, id)
);
CREATE INDEX ix_project_graph_edges_project ON harness.project_graph_edges (tenant_id, project_id);
CREATE INDEX ix_project_graph_edges_from ON harness.project_graph_edges (tenant_id, from_node_id);
CREATE INDEX ix_project_graph_edges_to ON harness.project_graph_edges (tenant_id, to_node_id);
ALTER TABLE harness.project_graph_edges ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.project_graph_edges FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.project_graph_edges
    USING (tenant_id = current_setting('app.tenant_id', true));

CREATE TABLE harness.project_graph_snapshots
(
    tenant_id char(26) NOT NULL,
    project_id char(26) NOT NULL,
    graph_version integer NOT NULL CHECK (graph_version > 0),
    reason text NOT NULL,
    node_count integer NOT NULL CHECK (node_count >= 0),
    edge_count integer NOT NULL CHECK (edge_count >= 0),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, project_id, graph_version)
);
ALTER TABLE harness.project_graph_snapshots ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.project_graph_snapshots FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON harness.project_graph_snapshots
    USING (tenant_id = current_setting('app.tenant_id', true));
