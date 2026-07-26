-- Migration 0078: Tabela de documentos vetoriais e embeddings semânticos (Fase 6)
CREATE TABLE IF NOT EXISTS harness.vector_embeddings (
    id TEXT NOT NULL PRIMARY KEY,
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    document_type TEXT NOT NULL,
    content TEXT NOT NULL,
    embedding_json TEXT NOT NULL,
    metadata_json TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_vector_embeddings_tenant_project ON harness.vector_embeddings(tenant_id, project_id);
CREATE INDEX IF NOT EXISTS idx_vector_embeddings_doc_type ON harness.vector_embeddings(tenant_id, document_type);
