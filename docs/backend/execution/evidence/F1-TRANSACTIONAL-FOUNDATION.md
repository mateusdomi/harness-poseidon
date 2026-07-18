# Evidência F1 — fundação transacional Inbox/Outbox/ledger

- Executado em: 2026-07-18T13:49:28Z
- Providers: SQLite dispatcher e PostgreSQL 18.4/Npgsql 10.0.3
- Resultado: verde

`IFoundationTransactionStore` recebe comando tipado e valida IDs ULID, nomes, idempotency key e hash. Uma única transação cria Tenant, Organização, Projeto e usuário local, anexa o primeiro elo do ledger SHA-256, cria a mensagem Outbox e persiste a receipt na Inbox.

O mesmo teste comportamental executou dez comandos concorrentes idênticos em cada provider: exatamente um receipt teve `Replay=false` e nove tiveram `Replay=true`. O snapshot final foi `1 tenant / 1 organização / 1 projeto / 1 usuário / 1 Inbox / 1 Outbox / 1 ledger`. Todos os receipts compartilharam sequência 1 e o hash recalculado deterministicamente.

Reutilizar a mesma chave com outro `messageHash` lançou `IdempotencyConflictException` e não alterou contagens. Um segundo comando com IDs novos, mas colisão deliberada no ID Outbox, falhou por constraint e fez rollback de Tenant, Organização, Projeto, usuário e ledger; o snapshot permaneceu idêntico.

SQLite serializa tudo pelo `SqliteWriteDispatcher`. PostgreSQL usa `pg_advisory_xact_lock(hashtextextended(tenant:key))` antes de ler a Inbox. Um ensaio inicial PostgreSQL encontrou SQLSTATE 42601 porque Npgsql não prepara múltiplos INSERTs parametrizados no mesmo command; os statements foram separados mantendo a mesma transaction, e a repetição passou.

Gate: build 0 warnings/0 errors; comportamento executado nos dois providers; `tools/backend/verify.sh` verde com 51/51 testes (integration 9). Cleanup PostgreSQL e SQLite ficou vazio e nenhuma área da Kimi foi alterada.
