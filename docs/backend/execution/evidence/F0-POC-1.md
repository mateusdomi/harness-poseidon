# Evidência F0 PoC-1 — SQLite WAL e dispatcher único

- Executado em: 2026-07-18T11:57:38Z
- Ambiente: macOS arm64, .NET SDK 10.0.302, EF Core SQLite 10.0.10
- Resultado: verde

## Invariantes exercitadas

- 24 produtores concorrentes, 40 escritas por produtor: 960 inserts confirmados.
- Canal multi-writer/single-reader: máximo observado de callbacks de escrita ativos = 1.
- `PRAGMA journal_mode` = `wal`.
- `PRAGMA foreign_keys` = `1`.
- `PRAGMA busy_timeout` = `5000` ms.
- Nenhuma `SqliteException`/`SQLITE_BUSY`, perda ou duplicação.
- Banco, WAL e SHM temporários removidos ao final; busca posterior encontrou zero `.db`, `.db-wal` ou `.db-shm`.

## Comandos e saídas verificáveis

```bash
tools/backend/dotnet.sh restore Harness.sln --use-lock-file --force-evaluate
tools/backend/dotnet.sh build tests/Harness.ConcurrencyTests/Harness.ConcurrencyTests.csproj --configuration Release --no-restore
tools/backend/dotnet.sh test tests/Harness.ConcurrencyTests/Harness.ConcurrencyTests.csproj --configuration Release --no-build --no-restore --logger 'console;verbosity=normal'
```

Exit codes: 0. Saída alvo: 2 testes aprovados, incluindo `ConcurrentProducersAreSerializedWithoutBusyFailures`; duração observada inicial de 402 ms.

O teste alvo foi repetido cinco vezes adicionais com exit code 0; cada repetição aprovou 1/1 teste em 45–47 ms. Depois, `tools/backend/verify.sh` terminou com exit code 0, build com 0 warnings/0 errors e 33 testes verdes nas seis suítes.

## Gate de segurança observado

O primeiro restore falhou (exit code 1) por `NU1903`: `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 estava na faixa de vulnerabilidade alta GHSA-2m69-gcr7-jv3q (`<= 2.1.11`). O audit não foi suprimido. A dependência nativa foi fixada centralmente em 3.53.3, o restore/audit passou e todos os lockfiles afetados foram regenerados.
