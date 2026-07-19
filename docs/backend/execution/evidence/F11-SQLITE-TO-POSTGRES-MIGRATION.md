# F11 — Migração de dados SQLite → PostgreSQL

Atualizado em: 2026-07-19. Evidência executada nesta sessão (contêiner PostgreSQL gerenciado real).

## Entregável

Ferramenta tipada `SqliteToPostgresMigrator` (projeto novo `src/Harness.Persistence.Migration`)
que migra todo o dado de domínio de um banco SQLite (modo pessoal) para um banco PostgreSQL
(modo servidor) com esquema em head, preservando isolamento por tenant, versões de concorrência
otimista (`version`), as cadeias de hash do ledger append-only, Inbox/Outbox e idempotência.

### Desenho (por que é robusto e tipado)

- **Dirigida pelo esquema vivo**: `PostgresSchemaReader` introspecta `pg_catalog` (tabelas,
  colunas físicas por `attnum`, chaves primárias, FKs). A cobertura é exatamente o conjunto de
  tabelas que as migrations PG criaram — nunca uma lista fixa que possa divergir.
- **Ordem topológica de FKs**: as tabelas são copiadas em ordem topológica (grafo de FKs entre
  tabelas é um DAG comprovado). Linhas de tabelas com auto-referência
  (`document_versions.supersedes_id`, `instruction_versions`, `solicitations`) são reordenadas por
  topologia de linhas (pai antes do filho), suportando chaves compostas.
- **Tipagem estrita**: cada coluna física do PostgreSQL é mapeada para um `PgColumnKind` fechado;
  qualquer tipo não mapeado faz a ferramenta falhar fechada (`MigrationSchemaException`), sem
  truncamento silencioso. Booleanos SQLite (0/1) viram `boolean`; timestamps ISO viram
  `timestamptz`; texto JSON vira `jsonb`; inteiros `version` são copiados verbatim.
- **Idempotência + atomicidade fail-closed**: toda linha é inserida com `ON CONFLICT (<pk>) DO
  NOTHING` dentro de uma única transação e, no conflito, todos os valores são comparados com
  `IS NOT DISTINCT FROM`. Somente uma linha byte/semanticamente equivalente é replay; o mesmo PK
  com conteúdo diferente lança `MigrationDataConflictException` sem incluir valores na mensagem e
  desfaz a transação. Drift de tabela/coluna também falha antes de aceitar migração parcial. Os
  gatilhos append-only (ledger, histórico de
  documentos) rejeitam apenas UPDATE/DELETE — INSERT é permitido — então as cadeias de hash são
  copiadas verbatim e permanecem verificáveis.
- **Catálogos em arquivo**: conteúdos de documentos/anexos/referências ficam no filesystem; a
  ferramenta migra apenas os metadados (caminho relativo + SHA-256). Cópia da árvore de arquivos é
  passo operacional explícito à parte (registrado, não silencioso).

## Cobertura de tabelas

**Todas as 80 tabelas base do schema `harness` foram cobertas; nenhuma diferida.** O teste afirma
`report.Deferred` vazio e `report.TablesCovered == COUNT(*)` de tabelas base do schema. Inclui:
tenants, organizations, projects, local_users (com `role`/`external_subject`), inbox_messages,
outbox_messages (+ dispatch), audit_ledger, toda a cadeia de trabalho
(solicitations→demands→work_tasks→instruction_versions→work_attempts→work_evidence→work_reviews),
workflows/runs (10 tabelas de definição/execução), documents+versions+classifications+approvals+
transitions, catálogos de agentes/ferramentas/providers/notificações, realtime, run targets,
licenças (+ assinadas), channel links, durable execution e projeções do quadro.

A tabela `harness_poc.work_items` (schema `harness_poc`, PoC) fica fora de escopo por não ser
domínio; a ferramenta migra apenas o schema `harness`.

## Teste (real, dual-provider)

`tests/Harness.IntegrationTests/Postgres/SqliteToPostgresMigrationTests.cs`
(`[Collection("managed-postgres")]`, reusa a `ManagedPostgresFixture` existente — contêiner
PostgreSQL 18.4 gerenciado, sem rede/cota externa).

Semeia um SQLite representativo: **2 tenants**, versões OCC não-zero (tenant v2/v3, project v4,
local_user v7, document v4), uma **cadeia de versões de documento auto-referente (v2→v1)**, cadeia
de trabalho e workflow completos, Inbox/Outbox/realtime, e **dois ledgers de auditoria multi-elo**
(3 e 2 eventos) com hashes reais calculados por `AuditLedgerHash`.

Asserções verdes:

- Contagem de linhas por tabela em PG == origem SQLite (todas as tabelas cobertas).
- Cada linha de origem é inserida **ou** reconhecida como idêntica (linhas de catálogo que as
  próprias migrations PG semeiam, ex. `agent_definitions` = 6 puladas, 0 duplicadas). `tenants`
  inseridas fresh = 2.
- Versões OCC preservadas verbatim (tenant, project, local_user, document).
- `local_users.external_subject` e contagem de demandas por tenant reidratam idênticos.
- Cadeia auto-referente `document_versions` v2 aponta para v1 após a migração.
- **A cadeia de hash do ledger reverifica em PostgreSQL** após normalização `jsonb`: recomputa
  `AuditLedgerHash.Compute` sobre o payload lido do PG e casa `event_hash`/`previous_hash` por
  tenant (5 eventos verificados).
- **Reexecutar é no-op**: segunda migração insere 0 linhas, tudo `SkippedExisting`, contagens
  inalteradas, ledger ainda íntegro.
- **Conflito não é replay**: após adulterar o nome de um tenant no destino, nova execução lança
  `MigrationDataConflictException`, identifica apenas a tabela e não expõe o ID/conteúdo.

## Pipeline (`tools/backend/verify.sh` — exit 0)

- Build Release: **0 Aviso(s) / 0 Erro(s)**.
- Testes backend: **234/234** verdes — Unit 121, Concurrency 3, Architecture 7, Contract 28,
  Recovery 5, Integration 70 (era 69; +1 = este teste de migração).
- `scan-secrets.sh`: limpo.
- Docker: zero órfãos `com.harness.managed=true` (contêiner/volume/rede) após a execução.

## Correção adicional revelada pelo gate

O gate integral reproduziu uma corrida entre o observador de saída e o endpoint de stop/cleanup de
run targets: ambos podiam dispor o mesmo `Process`, fazendo `WaitForExitAsync` lançar “No process is
associated with this object”. A remoção atômica da entrada agora transfere explicitamente a posse
do `Dispose`; o cenário real .NET/Node/Python passou três vezes consecutivas. A asserção absoluta
da PoC de Git permanece intacta: o repositório oficial contém somente `main` e `develop`.
