-- CAT-02: proprietário (owner) da definição de agente. Distingue as definições canônicas
-- built-in ('system') das definições criadas por um tenant (owner NULL até que o produto
-- passe a atribuí-lo). O conteúdo canônico completo das personas (persona, missão,
-- princípios, entregáveis, critérios, estilo, limitações, stacks, esforço, time,
-- ator/crítico, risco) é semeado de forma idempotente na inicialização do Host pelo
-- BuiltInAgentDefinitionSeeder — este passo apenas adiciona a coluna.
ALTER TABLE agent_definitions ADD COLUMN owner TEXT NULL
    CHECK(owner IS NULL OR length(owner) BETWEEN 1 AND 50);

CREATE INDEX ix_agent_definitions_owner ON agent_definitions(owner, id);
