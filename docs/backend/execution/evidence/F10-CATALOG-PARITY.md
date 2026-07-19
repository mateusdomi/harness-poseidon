# Evidência F10-2 — paridade PostgreSQL dos catálogos semeados

Data: 2026-07-19.

Segunda onda da paridade PostgreSQL do modo servidor:

- **Migrations PG 0015–0017** (`agent_catalog`, `tool_catalog`, `provider_catalog`): transcrição fiel das SQLite 0018/0020/0021 com tipos PG (`jsonb`+`jsonb_typeof`, `boolean`, `numeric`, `timestamptz`), **seeds idênticos com os mesmos ULIDs canônicos** (6 definições de agentes, skills/tools/plugins/MCP, vínculos das definições) e o `INSERT..SELECT` do Chief por projeto adaptado (`+ interval '1 minute'`). Uma colisão real foi detectada pela bateria e corrigida: o `ux_projects_tenant_id` já existia desde a migration PG 0005 — o ALTER duplicado foi removido.
- **3 stores PG novos**: `PostgresAgentCatalogStore` (definições + agentes com filtros e paginação), `PostgresToolCatalogStore` (quatro catálogos, `UpdateAsync` com validações idênticas, `tool.statusChanged` condicional e ledger/outbox) e `PostgresProviderCatalogStore` (lazy-seed por tenant com `ON CONFLICT DO NOTHING`, `UpdateAsync` dos 4 recursos, `SyncAsync` e eventos `quota.updated`/`audit.eventAppended` idênticos).
- **`CatalogStoreBehavior`** provider-neutro: exatamente as 6 definições canônicas de agentes; catálogos de ferramentas semeados e navegáveis; providers/modelos/budgets lazy-semeados por tenant com budget global e por conta. Verde no SQLite (banco novo) e na bateria PostgreSQL do container gerenciado (**17 migrations reais aplicadas**, reaplicação no-op).

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 220/220; PG `17→0`; zero Docker órfão. Restam para as próximas ondas F10: conversas/chief turn pipeline, quadro/projeções, workflow catalog projection, aprovações, notificações/settings, prototipação, run targets, licenças, canais, execução isolada — e então o switch `Harness:Database:Provider` no Host; OIDC/Entra por último (única dependência externa).
