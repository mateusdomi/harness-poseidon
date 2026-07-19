# Evidência F11-5 — matriz formal de resiliência

Data: 2026-07-19.

## Gate reproduzível

`tools/backend/verify-resilience.sh` consolida as provas críticas de release, usando somente testes
determinísticos/fakes e containers PostgreSQL com label `com.harness.managed=true`. O script faz
restore locked, build Release, executa os cenários abaixo e falha se restar container, volume ou
network gerenciado.

| Capacidade | Prova executada | Resultado observado |
|---|---|---|
| Upgrade | `SqliteMigrationUpgradeTests` | prefixos 10, 22 e 33 avançaram a 34, com reexecução idempotente |
| Backup/restore local | `LocalOperationsApiTests` | snapshot SQLite+catálogo, mutação posterior, restore real, entidade posterior removida, diagnóstico e auditoria |
| Migração pessoal→servidor | `SqliteToPostgresMigrationTests` | 80 tabelas, 2 tenants, OCC/ledger/self-reference, replay no-op e conflito divergente fail-closed |
| Carga multiusuário | `PostgresMultiuserLoadTests` | 30 usuários simultâneos, 1 tenant, 30 orgs/projetos e 429 observado |
| Isolamento/RBAC | `PostgresMultiuserLoadTests` | sessão cruzada 403, identidade forjada 404 e sessão ausente recusada |
| Recuperação dual-provider | `ProductionDurableExecutionRecoveryTests` | SIGKILL real, checkpoint 3/6, fencing crescente, retomada 6/6 e ledger completo em SQLite/PostgreSQL |
| Recuperação de composição | `IsolatedAttemptRecoveryTests` | todos os estágios claim→branch→worktree→sandbox→execução→cleanup reconciliados sem duplicação |

## Execução desta sessão

- Integração focada: **6/6** verdes em 8,7 s (3 upgrades + backup/restore + migração + carga/isolamento).
- Recovery focado: **3/3** verdes em 4,2 s (SQLite SIGKILL, PostgreSQL SIGKILL e composição por estágio).
- Gate integral anterior: **234/234 backend**, **331/331 frontend**, build Release 0 warnings/0 erros.
- Cleanup: zero container, volume ou network com `com.harness.managed=true`.

Essa matriz é uma seleção do mesmo código de teste já incluído em `tools/backend/verify.sh`; não
duplica cenários nem substitui o gate integral.
