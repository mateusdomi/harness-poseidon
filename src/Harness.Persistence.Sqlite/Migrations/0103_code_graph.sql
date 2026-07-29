-- GRAFO DE CÓDIGO DERIVADO (B6) — o índice que permite conferir o plano contra o código.
--
-- Tudo aqui é DERIVADO das fontes: nenhuma linha destas tabelas é cadastrada por pessoa ou por
-- agente. É o que separa este índice do mapa de arquitetura semeado à mão — um mapa declarado
-- envelhece em silêncio e continua parecendo verdade; um derivado erra junto com o código, e
-- reconstruir corrige.
--
-- Por isso a única escrita prevista é SUBSTITUIR o par (projeto, linguagem) inteiro. Não existe
-- "atualizar um nó": derivado que aceita remendo incremental deixa de corresponder à fonte no
-- primeiro remendo errado.
--
-- digest x source_revision: o primeiro é a impressão digital do CONTEÚDO do grafo (mesma estrutura
-- indexada => mesmo digest, o que responde "o grafo mudou?"); a segunda é a revisão do código de
-- onde ele saiu. Guardar só o digest deixaria um índice velho indistinguível de um atual, e medir
-- raio de impacto contra código que já mudou é pior que não medir: parece resposta.
CREATE TABLE code_graph_snapshots
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    language TEXT NOT NULL CHECK (length(language) BETWEEN 1 AND 40),
    digest TEXT NOT NULL CHECK (length(digest) = 64),
    node_count INTEGER NOT NULL CHECK (node_count >= 0),
    edge_count INTEGER NOT NULL CHECK (edge_count >= 0),
    files_indexed INTEGER NOT NULL CHECK (files_indexed >= 0),
    -- Até onde os diagnósticos desta derivação valem como afirmação sobre o build. 'syntax_only' é
    -- o alcance honesto de uma compilação sintética: arquivo que não parseia não compila em build
    -- nenhum, enquanto erro semântico pode ser apenas referência que a passada não tinha.
    diagnostic_scope TEXT NOT NULL CHECK (diagnostic_scope IN ('syntax_only','semantic')),
    error_count INTEGER NOT NULL CHECK (error_count >= 0),
    source_revision TEXT NULL
        CHECK (source_revision IS NULL OR length(source_revision) <= 80),
    built_at TEXT NOT NULL,
    PRIMARY KEY (tenant_id, project_id, language),
    FOREIGN KEY (tenant_id, project_id) REFERENCES projects(tenant_id, id)
);

CREATE TABLE code_graph_nodes
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    language TEXT NOT NULL,
    node_id TEXT NOT NULL CHECK (length(node_id) BETWEEN 1 AND 400),
    symbol TEXT NOT NULL CHECK (length(symbol) BETWEEN 1 AND 400),
    kind INTEGER NOT NULL CHECK (kind >= 0),
    file_path TEXT NOT NULL CHECK (length(file_path) <= 400),
    module TEXT NOT NULL CHECK (length(module) <= 200),
    PRIMARY KEY (tenant_id, project_id, language, node_id),
    FOREIGN KEY (tenant_id, project_id, language)
        REFERENCES code_graph_snapshots(tenant_id, project_id, language) ON DELETE CASCADE
);

CREATE TABLE code_graph_edges
(
    tenant_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    language TEXT NOT NULL,
    from_node_id TEXT NOT NULL,
    to_node_id TEXT NOT NULL,
    kind INTEGER NOT NULL CHECK (kind >= 0),
    PRIMARY KEY (tenant_id, project_id, language, from_node_id, to_node_id, kind),
    FOREIGN KEY (tenant_id, project_id, language)
        REFERENCES code_graph_snapshots(tenant_id, project_id, language) ON DELETE CASCADE
);

-- A pergunta que o raio de impacto faz é sempre a inversa ("quem depende deste nó?"), então o
-- índice útil é pelo DESTINO da aresta.
CREATE INDEX ix_code_graph_edges_target
    ON code_graph_edges (tenant_id, project_id, language, to_node_id);

-- E a entrada do cálculo é um caminho de arquivo tocado por um card.
CREATE INDEX ix_code_graph_nodes_file
    ON code_graph_nodes (tenant_id, project_id, language, file_path);
