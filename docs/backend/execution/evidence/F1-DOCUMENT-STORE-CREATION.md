# Evidência F1 — criação e leitura transacional de documentos

- Executado em: 2026-07-18T16:45:05Z
- Incremento: F1-DOC-1c.1
- Resultado: verde

`IDocumentStore` introduz contrato comum de criação e leitura. A borda valida ULIDs canônicos, catálogo fechado de kind/autor, classificações únicas e ordinalmente ordenadas, path relativo sem traversal, SHA-256 maiúsculo, textos normalizados e idempotency key.

O SQLite executa toda a transação no dispatcher único. O PostgreSQL serializa a chave idempotente e a cauda do ledger com advisory locks. Em ambos, documento v1, referência de conteúdo imutável, classificações, Inbox, ledger encadeado e Outbox `document.stateChanged` são gravados na mesma transação. O conteúdo continua fora do banco: somente path catalogado e hash são persistidos.

O comportamento provider-neutral disparou dez criações simultâneas e observou exatamente uma aplicação e nove replays com o mesmo ledger hash/sequence/outbox id. A mesma chave com payload diferente lançou `IdempotencyConflictException` e não alterou o snapshot. Path com traversal foi recusado antes da persistência. A leitura integral recompôs metadados, classificações, versão v1 e coleções vazias de aprovação/histórico.

Execuções focadas: SQLite 1/1 e PostgreSQL 1/1, ambos com build Release 0 warnings/0 errors. O PostgreSQL foi executado após inventário de 11 containers de terceiros parados, 14 volumes, 7 networks e portas; cleanup final comprovou zero recursos `com.harness.managed=true`. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build 0 warnings/0 errors e 92/92 testes (`Unit 63`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

O próximo incremento adiciona append de versão com optimistic concurrency, supersession imutável e finalização transacional de Inbox, ledger e Outbox; depois segue classificação, lifecycle e aprovações.
