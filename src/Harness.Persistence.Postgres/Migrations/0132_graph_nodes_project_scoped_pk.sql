-- Migration 0132: PK dos nós/arestas do grafo passa a incluir o projeto (ver SQLite homônima).
ALTER TABLE harness.project_graph_edges
    DROP CONSTRAINT project_graph_edges_tenant_id_from_node_id_fkey,
    DROP CONSTRAINT project_graph_edges_tenant_id_to_node_id_fkey;
ALTER TABLE harness.project_graph_nodes DROP CONSTRAINT project_graph_nodes_pkey;
ALTER TABLE harness.project_graph_nodes ADD PRIMARY KEY (tenant_id, project_id, id);
ALTER TABLE harness.project_graph_edges DROP CONSTRAINT project_graph_edges_pkey;
ALTER TABLE harness.project_graph_edges ADD PRIMARY KEY (tenant_id, project_id, id);
ALTER TABLE harness.project_graph_edges
    ADD FOREIGN KEY (tenant_id, project_id, from_node_id)
        REFERENCES harness.project_graph_nodes(tenant_id, project_id, id),
    ADD FOREIGN KEY (tenant_id, project_id, to_node_id)
        REFERENCES harness.project_graph_nodes(tenant_id, project_id, id);
