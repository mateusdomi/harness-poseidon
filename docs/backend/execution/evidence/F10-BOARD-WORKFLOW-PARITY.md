# Evidência F10-4 — paridade PostgreSQL do quadro, aprovações, catálogo de workflows e cockpit

Data: 2026-07-19.

Quarta onda da paridade PostgreSQL — a camada de projeções do MVP:

- **Migrations PG 0020–0023**: projeção do quadro (solicitações/demandas/tarefas/instruções/tentativas + `attempt_events`, verificada coluna a coluna contra a base 0005), aprovações gerais com máquina de estados, `operational_state` das tentativas e a projeção do catálogo de workflows (bindings, aceites de risco, colunas de comando das versões).
- **Stores PG novos**: `PostgresWorkBoardStore` (+Mutations) com a matriz de estados do quadro e payloads idênticos; `PostgresDocumentCatalogStore` (+Approvals) incluindo a resolução transacional de gate com promoção de objective; `PostgresWorkflowCatalogStore` (+Mutations) com `PublishVersionAsync` serializado por advisory lock e `SetOperationModeAsync` auditado; `PostgresCockpitDigestStore` (digest as-of determinístico).
- **Paridade retroalimentada**: com as colunas novas, `MaterializeChiefDemandsAsync` PG passou a gravar o registro completo (idêntico ao SQLite) e o drain do orquestrador voltou a usar `board_state`/`operational_state` reais.
- **`BoardWorkflowProjectionBehavior`** provider-neutro: solicitação→demanda→tarefa com backing interno, estado inicial `backlog` e movimento `→ready` pela matriz (o cenário inicialmente presumiu `ready` e o comportamento real dos DOIS providers o corrigiu), instrução imutável única; definição publicada pela autoridade projetada como template, binding `semiautonomous` com aceite e troca para `autonomous` com nova aceitação. Verde no SQLite e na bateria PostgreSQL (**23 migrations reais**, reaplicação no-op).

Pendência consciente para a onda final de work chain: `PostgresWorkChainStore.Mutations` ainda não mantém `board_state`/`operational_state`/`attempt_events` em start/complete/review (attempts novos ficam nos defaults `backlog`/`queued` no PG até esse porte).

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 220/220; PG `23→0`; zero Docker órfão.
