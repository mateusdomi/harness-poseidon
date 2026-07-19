# Evidência F10-5 — paridade PostgreSQL completa e Host em modo servidor

Data: 2026-07-19.

Fechamento do épico de paridade PostgreSQL (ondas 1–5) e do switch de provider:

- **Migrations PG 0024–0032** (notificações/settings, prototipação, run targets, licenças, attempt workspaces, anexos de solicitação, assets de referência, licenças assinadas, canais): transcrições fiéis com adaptações idiomáticas registradas — triggers→CHECK, `INSERT OR IGNORE`→`ON CONFLICT DO NOTHING`, `signed_licenses.document_json` como `json` puro (jsonb normalizaria e quebraria a assinatura Ed25519 — decisão de integridade).
- **9 stores PG finais** + retro-paridade: `PostgresLocalProfileStore` provisiona settings, `PostgresProjectStore` lê/grava prototipação, `PostgresWorkChainStore.Mutations` mantém `board_state`/`operational_state`/`attempt_events` — **todas as 31 interfaces de persistência agora têm implementação dual**.
- **Switch `Harness:Database:Provider`** no Host: `postgres` exige `Harness:Database:ConnectionString`, registra `NpgsqlDataSource` + `PostgresMigrationHostedService` + o conjunto completo de stores PG; `sqlite` (padrão) permanece intacto; backup/restore/diagnóstico locais respondem `server_mode_operations` no modo servidor (responsabilidade do PostgreSQL gerenciado).
- **`PostgresServerModeHostTests`**: o Host inteiro bootou contra o container PostgreSQL gerenciado — **32 migrations aplicadas**, health verde, **os 6 templates canônicos semeados pela mesma seed idempotente**, e o fluxo completo do MVP executou de ponta a ponta em PG: perfil → organização → projeto (com Chief) → conversa → turno do Chief processado pelo worker durável → **demanda materializada** → guard de operações locais respondendo 409 de modo servidor. Verde na primeira execução.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral **221/221** (`Unit 121`, `Integration 58`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); PG `32→0` idempotente; zero Docker órfão.

Restante do F10 sem dependência externa: RBAC/ABAC multiusuário, rate limit e teste de carga com 30 usuários + isolamento adversarial + failover — próximos passos; **OIDC/Entra ID é a única dependência externa** e será solicitada ao usuário quando for o último item.
