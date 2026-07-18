# Evidência F1 — criação transacional da cadeia

- Executado em: 2026-07-18T15:10:47Z
- Incremento: EP-09b.2a
- Resultado: verde

`IWorkChainStore` foi criado em Persistence.Abstractions com comando tipado, receipt e snapshot. O validator comum exige ULIDs canônicos, conteúdo dentro dos limites, critérios de aceite como array não vazio de strings, risk tier fechado, peso positivo e SHA-256 correspondente ao conteúdo imutável da instrução.

`SqliteWorkChainStore` executa pelo dispatcher único; `PostgresWorkChainStore` serializa idempotência por `(tenant, key)` e o ledger por tenant com advisory locks transacionais. A criação grava Solicitação, Demanda, Tarefa pronta e Instrução v1, depois anexa ledger, Outbox e receipt na Inbox antes do commit. A leitura recompõe o snapshot inicial e contadores de attempts, evidências e reviews.

O mesmo teste de comportamento executou nos dois providers:

- 10 criações concorrentes idênticas produziram exatamente 1 aplicação e 9 replays;
- todos os receipts compartilharam ledger hash e Outbox ID;
- snapshot preservou IDs, conteúdo, risco, peso, estado `ready`, instrução v1/hash e contadores zero;
- reutilização da chave com comando diferente lançou `IdempotencyConflictException` e não alterou o snapshot;
- solicitação inexistente retornou ausência, sem efeito colateral.

Na primeira execução PostgreSQL, Npgsql rejeitou múltiplas instruções parametrizadas em um único prepared statement (`42601`). Os inserts foram separados em comandos individuais dentro da mesma transação, preservando atomicidade; o teste focado passou em seguida.

Evidência: SQLite focado 1/1, PostgreSQL focado 1/1; `tools/backend/verify.sh` exit code 0; build Release 0 warnings/0 errors; suíte 74/74 (`Unit 46`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 12`).

Esta fatia não declara o store completo. Start de tentativa, completion/evidências, review, optimistic concurrency e reidratação integral são o próximo incremento EP-09b.2b.
