# Evidência F1 — schema de fundação dual-provider

- Executado em: 2026-07-18T13:37:34Z
- Providers: SQLite 3.53.3 via Microsoft.Data.Sqlite/EF Core 10.0.10; PostgreSQL 18.4 via Npgsql/EF provider 10.0.3
- Resultado: verde

## Conteúdo validado

As migrations específicas de provider criaram o mesmo modelo conceitual:

- `tenants`, `organizations`, `projects`, `local_users` com IDs ULID `char/text(26)`, `version >= 0`, UTC e relações tenant/organização;
- `inbox_messages` com chave composta `(tenant, idempotencyKey)` e hash SHA-256;
- `outbox_messages` com payload, tentativas, despacho pendente e índice parcial;
- `audit_ledger` com sequência única por tenant, hash anterior, hash do evento e índice ordenado.

SQLite usa migration runner próprio dentro do `SqliteWriteDispatcher`; a primeira aplicação retornou 1 e a repetição 0. PostgreSQL usa advisory lock transacional e histórico `harness.schema_migrations`; com a migration PoC preexistente, retornou 2 e depois 0. Nenhuma query ou migration é compartilhada entre providers.

Os testes contaram exatamente sete tabelas de fundação em cada provider, inseriram Tenant→Organização→Projeto e usuário local válidos e provaram que projeto referenciando organização inexistente é rejeitado com constraint FK (`SQLite error 19`; PostgreSQL SQLSTATE 23503).

## Gate

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter 'FullyQualifiedName~SqliteFoundationMigrationsTests|FullyQualifiedName~PostgresSkipLockedPocTests'
tools/backend/verify.sh
```

Testes focados: 2/2 verdes. Gate completo: restore locked, format, build Release com 0 warnings/0 errors e 50/50 testes (integration 8). O fixture PostgreSQL removeu container/network/volume/imagem gerenciados; o banco SQLite temporário foi removido após dispose. Recursos de terceiros e áreas da Kimi permaneceram intactos.
