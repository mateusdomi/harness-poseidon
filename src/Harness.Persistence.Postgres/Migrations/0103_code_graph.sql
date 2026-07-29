-- Paridade com o SQLite: grafo de código DERIVADO das fontes, substituído inteiro a cada
-- reconstrução. Não existe "atualizar um nó" — derivado que aceita remendo incremental deixa de
-- corresponder à fonte e volta a ser declaração que envelhece em silêncio.
CREATE TABLE harness.code_graph_snapshots
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    language text NOT NULL CHECK (length(language) BETWEEN 1 AND 40),
    digest text NOT NULL CHECK (length(digest) = 64),
    node_count integer NOT NULL CHECK (node_count >= 0),
    edge_count integer NOT NULL CHECK (edge_count >= 0),
    files_indexed integer NOT NULL CHECK (files_indexed >= 0),
    diagnostic_scope text NOT NULL CHECK (diagnostic_scope IN ('syntax_only','semantic')),
    error_count integer NOT NULL CHECK (error_count >= 0),
    source_revision text NULL CHECK (source_revision IS NULL OR length(source_revision) <= 80),
    built_at timestamptz NOT NULL,
    PRIMARY KEY (tenant_id, project_id, language)
);

CREATE TABLE harness.code_graph_nodes
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    language text NOT NULL,
    node_id text NOT NULL CHECK (length(node_id) BETWEEN 1 AND 400),
    symbol text NOT NULL CHECK (length(symbol) BETWEEN 1 AND 400),
    kind integer NOT NULL CHECK (kind >= 0),
    file_path text NOT NULL CHECK (length(file_path) <= 400),
    module text NOT NULL CHECK (length(module) <= 200),
    PRIMARY KEY (tenant_id, project_id, language, node_id),
    FOREIGN KEY (tenant_id, project_id, language)
        REFERENCES harness.code_graph_snapshots(tenant_id, project_id, language) ON DELETE CASCADE
);

CREATE TABLE harness.code_graph_edges
(
    tenant_id text NOT NULL,
    project_id text NOT NULL,
    language text NOT NULL,
    from_node_id text NOT NULL,
    to_node_id text NOT NULL,
    kind integer NOT NULL CHECK (kind >= 0),
    PRIMARY KEY (tenant_id, project_id, language, from_node_id, to_node_id, kind),
    FOREIGN KEY (tenant_id, project_id, language)
        REFERENCES harness.code_graph_snapshots(tenant_id, project_id, language) ON DELETE CASCADE
);

CREATE INDEX ix_code_graph_edges_target
    ON harness.code_graph_edges (tenant_id, project_id, language, to_node_id);

CREATE INDEX ix_code_graph_nodes_file
    ON harness.code_graph_nodes (tenant_id, project_id, language, file_path);

ALTER TABLE harness.code_graph_snapshots ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.code_graph_snapshots FORCE ROW LEVEL SECURITY;
ALTER TABLE harness.code_graph_nodes ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.code_graph_nodes FORCE ROW LEVEL SECURITY;
ALTER TABLE harness.code_graph_edges ENABLE ROW LEVEL SECURITY;
ALTER TABLE harness.code_graph_edges FORCE ROW LEVEL SECURITY;

CREATE POLICY tenant_isolation ON harness.code_graph_snapshots
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));

CREATE POLICY tenant_isolation ON harness.code_graph_nodes
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));

CREATE POLICY tenant_isolation ON harness.code_graph_edges
    USING (tenant_id = current_setting('poseidon.tenant_id', true))
    WITH CHECK (tenant_id = current_setting('poseidon.tenant_id', true));
