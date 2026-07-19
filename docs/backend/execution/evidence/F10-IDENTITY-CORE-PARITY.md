# Evidência F10-1 — paridade PostgreSQL do núcleo identidade/org/projeto/auditoria

Data: 2026-07-19.

Primeira onda da paridade PostgreSQL do modo servidor, seguindo a decisão do usuário de avançar tudo que não depende de conta externa:

- **Migrations PG 0012–0014** (`local_profiles`, `organizations`, `projects`): transcrição fiel das SQLite 0009–0011 com tipos PG (`varchar`, `timestamptz`, `jsonb`), índices únicos parciais (`lower(email)`, `lower(slug)`, `upper(project_key)` com soft-delete) e backfills.
- **4 stores PG novos**: `PostgresLocalProfileStore` (guard global de perfil único do modo pessoal, provisionamento de tenant, OCC), `PostgresOrganizationStore` (slug único, conflitos de constraint → `AlreadyExists`), `PostgresProjectStore` (guard de organização, ledger encadeado + Outbox `project.created` idênticos ao SQLite) e `PostgresAuditEventStore` (AppendAsync/List/Get/VerifyIntegrity com canonicalização de JSON estável sob `jsonb`). Padrões PG da casa: `NpgsqlDataSource`, `$n` posicionais, `pg_advisory_xact_lock(hashtextextended(...))`, `TrimEnd()` em `char(n)`.
- **`IdentityCoreStoreBehavior`**: cenário provider-neutro (perfil→guard global→OCC/version conflict; organização→slug duplicado recusado; projeto→organização inexistente recusada→criação→soft delete com OCC; auditoria→2 appends→filtro→cadeia de hashes íntegra) executado no SQLite (banco novo) e na bateria PostgreSQL do container gerenciado (banco compartilhado — o comportamento cobre os dois regimes do guard).
- **2 bugs latentes corrigidos nos DOIS providers**: leitura de `last_active_at`/`last_activity_at` NULL para linhas provisionadas pela via F1 (agora `COALESCE(...,created_at)`) — era a causa raiz da fragilidade que o seeder de templates contornava no startup.

Omissões conscientes registradas (aguardam as próximas ondas de paridade): `profile_settings` (notificações), inserção do agente Chief na criação de projeto (catálogo de agentes) e colunas de prototipação em `harness.projects` — o `ProjectRecord.Prototyping` lê o default no PG até a onda de prototipação.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 220/220 (`Unit 121`, `Integration 57`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); PG `14→0` e SQLite `32→0`; zero Docker órfão. Próximas ondas F10: catálogos (agentes/ferramentas/providers), conversas/chief, quadro/projeções, e então o switch `Harness:Database:Provider` no Host; OIDC/Entra fica por último como única dependência externa.
