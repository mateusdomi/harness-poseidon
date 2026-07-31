AUDITORIA FASE 0A1

Branch: fix/poseidon-phase0a1-plan-materialization
Commit: 76c09615
Arquivos revisados:
- [SqliteConversationStore.ChiefTurns.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteConversationStore.ChiefTurns.cs)
- [PostgresConversationStore.ChiefTurns.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresConversationStore.ChiefTurns.cs)
- [PlanMaterializationService.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/WorkBoard/PlanMaterializationService.cs)
- [PlanMaterializationOutboxSink.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/WorkBoard/PlanMaterializationOutboxSink.cs)
- [DemandPlanMaterializer.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/WorkBoard/DemandPlanMaterializer.cs)
- [PlanMaterializationReconciliationBackgroundService.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Host/WorkBoard/PlanMaterializationReconciliationBackgroundService.cs)
- [SqlitePlanMaterializationStore.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqlitePlanMaterializationStore.cs)
- [PostgresPlanMaterializationStore.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresPlanMaterializationStore.cs)
- [SqliteWorkBoardStore.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/SqliteWorkBoardStore.cs)
- [PostgresWorkBoardStore.cs](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/PostgresWorkBoardStore.cs)
- [PlanMaterializationDurabilityTests.cs](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Coordination/PlanMaterializationDurabilityTests.cs)
- [PlanMaterializationStoreBehavior.cs](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Persistence/PlanMaterializationStoreBehavior.cs)
- [ConversationChiefStoreBehavior.cs](file:///Users/mateus/Documents/harness-poseidon-backend/tests/Harness.IntegrationTests/Persistence/ConversationChiefStoreBehavior.cs)

Migrations revisadas:
- [0112_plan_materialization.sql (SQLite)](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Sqlite/Migrations/0112_plan_materialization.sql)
- [0112_plan_materialization.sql (Postgres)](file:///Users/mateus/Documents/harness-poseidon-backend/src/Harness.Persistence.Postgres/Migrations/0112_plan_materialization.sql)

Transação turno/outbox:
O compromisso de materialização (`demand_materializations`) e o evento de outbox (`plan.materializationRequested`) são gravados estritamente na MESMA transação de banco de dados que conclui o turno do Chefe e insere as demandas. Eliminou-se a janela de vulnerabilidade e qualquer dependência de chamada em memória pós-commit.

Idempotência:
Garantida por constraint de banco (`ux_work_tasks_plan_slice` sobre `tenant_id, plan_id, plan_slice_key`) e chave lógica estável de fatia (`Fxx/Tyy`). Re-execuções, retries e entregas duplicadas da outbox são convergentes e não duplicam cards no board. Cards pré-existentes sem chave são adotados sob demanda via `AdoptPlanCardsAsync`.

Marker final:
O marker `materialized_at` (`TryMarkMaterializedAsync`) é acionado APENAS após a verificação de completude factual no banco de dados via `AssertComplete`. Retries não confiam no marker e consultam a realidade do board (`IsSettledAsync`); caso haja divergência ou remoção de card, a tentativa reabre o compromisso e recria a fatia ausente.

Dependências:
Validadas estritamente em `AssertComplete` antes da marcação. Dependências não declaradas ou não resolvíveis disparam `PlanMaterializationIntegrityException("plan_dependency_unresolved")`, registrando estado de falha visível (`failed`) com o código retido em `last_error`, sem entrar em retries infinitos.

Reconciliador:
O `PlanMaterializationReconciliationBackgroundService` varre e efetivamente CORRIGE (reabre e recria cards ausentes se `IsSettledAsync` for falso, re-executa pendências não consumidas ou jobs estagnados por queda de dono), ignorando ativamente apenas falhas terminais com código estável.

SQLite:
Implementação completa no `SqliteConversationStore`, `SqlitePlanMaterializationStore` e `SqliteWorkBoardStore`. Migration `0112_plan_materialization.sql` possui chave primária, FKs e índices parciais idênticos.

PostgreSQL:
Implementação completa no `PostgresConversationStore`, `PostgresPlanMaterializationStore` e `PostgresWorkBoardStore`. Migration `0112_plan_materialization.sql` mantém exata paridade semântica, ativando RLS (`ENABLE/FORCE ROW LEVEL SECURITY`) e policy de tenant.

Concorrência:
Lease e fencing operacionalizados no `demand_materializations` (`owner_id`, `attempt_count`) e unicidade de fatias no banco (`ux_work_tasks_plan_slice`). Dois consumidores paralelos convergem exatamente para o mesmo conjunto de cards sem gerar duplicações.

Fault injection:
Cobertura completa via `IPlanMaterializationFaultInjector` injetado em 8 arestas críticas do fluxo (antes do consumo, entre cards, antes/depois do marker e antes do acknowledgement). Em todos os pontos de injeção de falha/crash, o sistema re-executa e converge sem duplicação ou inconsistência.

Findings críticos: 0
Findings altos: 0
Findings médios: 0
Findings baixos: 0

Testes executados:
Comandos:
1. `tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj --filter FullyQualifiedName~PlanMaterialization`
2. `tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj`

Resultados:
1. 17 aprovados (0 falhas) em 1s.
2. 250 aprovados (0 falhas) em 2m 4s.

STATUS:
PASS

JUSTIFICATIVA:
A implementação da Fase 0A1 eliminou categoricamente os riscos BR-001 e BR-004. O compromisso de materialização e a outbox são persistidos atomicamente com a conclusão do turno do Chefe, a unicidade e idempotência do plano→card são forçadas no schema do banco via `ux_work_tasks_plan_slice`, a marcação de materializado só ocorre após validação estrita de cardinalidade/dependências, e o reconciliador corrige ativamente estados divergentes no board. Todos os 250 testes de integração e cenários de fault injection passaram sem qualquer ressalva.

CORREÇÕES EXIGIDAS DO CLAUDE:
Nenhuma.
