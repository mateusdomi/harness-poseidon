# Evidência F1 — stores duráveis da Outbox

- Executado em: 2026-07-18T17:20:37Z
- Incremento: F1-WRK-1b
- Resultado: verde

`SqliteOutboxStore` executa claims/mutações pelo dispatcher único. `PostgresOutboxStore` usa transações, seleção ordenada com `FOR UPDATE SKIP LOCKED` e lock de row em completion/failure. Ambos aplicam eligibility por agenda, lease expirável, fencing crescente, completion fenced, retry com backoff, dead-letter terminal, liberação de claims expirados e snapshot operacional.

O comportamento comum provisionou duas mensagens reais pela transação de fundação. Dez aquisições concorrentes retornaram exatamente duas leases distintas com token 1; as demais retornaram vazio. Snapshot intermediário: 0 pending/2 claimed. Após expiração, o reconciliador liberou 2 e os novos owners adquiriram tokens 2; completion do token 1 foi recusada.

Uma mensagem foi marcada dispatched. A outra registrou falha attempt 1, ficou indisponível antes do backoff, foi readquirida com token 3 e recusou a falha do owner/token 2. A falha attempt 2 tornou-a dead-letter. Snapshot final em ambos: 0 pending, 0 claimed, 1 dispatched, 1 dead-letter e 2 failures append-only. Mensagens terminais não foram readquiridas; completion de terminal retornou `AlreadyTerminal` e ID inexistente retornou `NotFound`.

Durante o primeiro teste SQLite, a fila vazia revelou que `Convert.ToString(null)` virava string vazia após as duas aquisições. A causa foi instrumentada/determinada e corrigida preservando o `null` retornado por `ExecuteScalarAsync`; a repetição passou sem alterar a política de concorrência.

Execuções focadas SQLite 1/1 e PostgreSQL 1/1 passaram após build Release 0 warnings/0 errors. Inventário Docker preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos `com.harness.managed=true`. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build 0 warnings/0 errors e 94/94 testes (`Unit 65`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

O próximo incremento conecta o store a um `OutboxDispatcherBackgroundService` cancelável e testável com sink fake, antes do sink SignalR.
