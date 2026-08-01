AUDITORIA FASE 1

## 0-E — Docker obrigatório
Análise:
- **Extinção do aceite inseguro**: O aceite de modo inseguro (`UnsafeModeAccepted`, `unsafe_mode_accepted_at` e a tabela `unsafe_execution_acceptances`) foi inteiramente removido do código backend C# e do banco de dados através da Migration `0116` em SQLite e Postgres (`DROP TABLE IF EXISTS unsafe_execution_acceptances; UPDATE profile_settings SET unsafe_mode_accepted_at = NULL`).
- **Sonda de Docker**: `ContainerRuntimeProbe` invoca `docker version --format "{{.Server.Version}}"`. A verificação atesta a comunicação efetiva com o servidor/daemon Docker (o cliente CLI responder a `docker version` não basta se o daemon estiver desligado).
- **Launcher e Doctor**: Ambos invocam as sondas de runtime e falham com a mesma mensagem de negócio acionável (`ContainerRuntimeProbe.Message` / `ContainerRuntimeCheck.Message`): *"Preciso do Docker para trabalhar com segurança — instale ou inicie o Docker e me chame de novo."*. O launcher aborta a inicialização (código de saída 3) antes de subir o Host, e o endpoint `/api/v1/agent-accounts/doctor` reporta `containerRuntimeReady: false`.
- **Caminho residual**: A avaliação de ferramentas (`ToolExecutionPolicy.Evaluate`) nega qualquer invocação de risco `High` ou `Critical` quando `!SandboxActive` com a razão `sandbox_required`. Não há caminho residual permitindo execução de agentes fora da sandbox atestada.
- **Detecção secundária**: Bundles estáticos pré-compilados do frontend SPA (`wwwroot/assets/onboarding-page-CreXtRGx.js` e `settings-page-BzaQg1Jq.js`) contêm referências visuais/formulários legados a `unsafeAccepted`, mas as APIs do backend recusam tentativas de execução local sem Docker (HTTP 409).

Findings críticos / altos / médios / baixos:
- Críticos: 0
- Altos: 0
- Médios: 0
- Baixos: 1 (Presença de strings/propriedades legadas de aceite nos arquivos JS minificados do frontend em `wwwroot/assets`)

STATUS: PASS

---

## 1A — retomada por checkpoint
Análise:
- **Captura**: O `ExecutionCheckpointService` foi acoplado aos dois pontos essenciais de interrupção: na falha da execução (`TryCaptureCheckpointAsync` em `AgentRunOrchestrator.cs:873`) e na recuperação de tentativas órfãs travadas após queda do Host (`TryCaptureOrphanCheckpointAsync` em `RecoverAsync`).
- **Consumo do checkpoint**: Em `PrepareCheckpointResumeAsync`, o checkpoint é consumido via `checkpoints.ConsumeAsync` **antes** da montagem do prompt (`ContextBundleBuilder.BuildOrFallback`), prevenindo que duas tentativas simultâneas partam do mesmo checkpoint e colidam na mesma branch.
- **Retomada nula/recusada**: Quando não há checkpoint ou a política recusa, `PrepareCheckpointResumeAsync` retorna texto vazio, fazendo a tentativa recomeçar do zero explicitamente e sem falsas premissas de continuidade no prompt.
- **Isolamento de exceção**: As rotinas de captura envolvem a escrita em blocos `try/catch` generalistas (`catch (Exception) when (exception is not OperationCanceledException)`), registrando a telemetria `capture_failed` sem mascarar a falha original da execução e sem bloquear a devolução/liberação da worktree ao pool.

Findings críticos / altos / médios / baixos:
- Críticos: 0
- Altos: 0
- Médios: 0
- Baixos: 0

STATUS: PASS

---

## 1B — orçamento no despacho
Análise:
- **Determinismo**: A determinação do orçamento (`EffortPolicy.Decide`) é estritamente determinística com base nos parâmetros `WorkNature`, `RiskTier` e na constatação objetiva `isSmall`.
- **Imutabilidade no Plano**: O orçamento é gravado no momento do planejamento no campo `cards_json` (`DemandDecompositionPlanner`) e lido diretamente do card durante o despacho no `ChiefBacklogLoopService.cs` (`ReadCardBudgetAsync`), em vez de ser recalculado dinamicamente.
- **Escalonamento por esgotamento**: Quando um card atinge o teto de rodadas orçadas (`attemptHistory.Count >= budget.MaxRounds`), o `ChiefBacklogLoopService` chama `EscalateBudgetExhaustionAsync`, que emite um evento de auditoria (`card.effortBudgetExhausted`) com o histórico de rodadas e registra a métrica `poseidon.effort_budget.count(state=exhausted)`.
- **Compatibilidade regressiva**: Planos antigos criados sem orçamento desserializam com `Budget = null` e seguem o fluxo padrão sem causar erros ou interrupções.
- **Declaração de pendência (Assimetria de modelo)**: A assimetria de roteamento de modelos por capacidade (rotear chefe/revisão crítica para o modelo mais capaz) está expressamente declarada como pendente no commit `8692593e` (*"PENDENTE do 1B: a assimetria de modelo (chefe e revisao de risco alto no mais capaz) exige entrar no roteador de contas e nao foi implementada"*), sem disfarce ou alegação falsa de entrega.

Findings críticos / altos / médios / baixos:
- Críticos: 0
- Altos: 0
- Médios: 0
- Baixos: 0

STATUS: PASS

---

## 1C — veredito composto
Análise:
- **Ordem e Não-Compensação**: `LayeredVerificationPolicy.Evaluate` avalia estritamente na ordem `[Deterministic, Behavioral, Intent]`. O loop é interrompido no primeiro resultado não-aprovado (`Fail` ou `NotRun`), garantindo que camadas superiores (ex: Intenção) jamais compensem falhas em camadas inferiores (ex: Build/Testes).
- **Tratamento de camada não executada**: Qualquer camada com estado `LayerVerdict.NotRun` resulta imediatamente em `Approved = false` com `ReasonCode = "verification.layer_not_run"`.
- **Detalhamento e Severidade**: A resposta indica a camada exata de falha (`BlockedAt`), o código do motivo (`ReasonDeterministicFailed`, `ReasonBehavioralFailed`, `ReasonIntentFailed`) e a severidade correspondente (`blocker` para determinística, `major` para comportamental/intenção).
- **Proteção do Slot do Revisor**: A função `MayOccupyReviewer` verifica se a camada determinística passou (`Deterministic == Pass`). O `ChiefBacklogLoopService` invoca essa verificação antes de alocar um agente especialista/crítico para revisão.

Findings críticos / altos / médios / baixos:
- Críticos: 0
- Altos: 0
- Médios: 0
- Baixos: 0

STATUS: PASS

---

## 1D — MAST consumido
Análise:
- **Mapeamento de Correção**: `MastCorrectionPolicy.Evaluate` mapeia corretamente cada categoria taxonômica:
  - `SpecificationAndDesign`: `PreferSmallerSlices = true`, `RequireExplicitAcceptanceCriteria = true`, `ExtraReviewDepth = 0`.
  - `InterAgentMisalignment`: `PreferSmallerSlices = true`, `RequireExplicitAcceptanceCriteria = false`, `ExtraReviewDepth = 0`.
  - `VerificationAndTermination` (e default): `ExtraReviewDepth = 1`, `PreferSmallerSlices = false`, `RequireExplicitAcceptanceCriteria = false`.
- **Limiar de Sinal**: Ocorrências isoladas (contagem = 1) não geram sinal (`RecurrenceThreshold = 2`), retornando `HasSignal = false` e motivo `mast.no_recurrent_mode`.
- **Citação de Evidência e Auditoria**: O objeto `MastCorrection` devolve o texto descritivo de evidência (ex: `"O modo 'disobey_task_specification' (SpecificationAndDesign) apareceu 3 vezes..."`). No `PlanMaterializationService.cs`, a aplicação da correção grava o evento de auditoria `plan.mastCorrectionApplied`.
- **Resiliência a Falhas de Leitura**: Na leitura da distribuição MAST (`ReadMastCorrectionAsync`), exceções de I/O ou banco são capturadas (`catch (Exception) when (exception is not OperationCanceledException)`), retornando um fallback neutro (`HasSignal = false`) sem interromper a materialização do plano.

Findings críticos / altos / médios / baixos:
- Críticos: 0
- Altos: 0
- Médios: 0
- Baixos: 0

STATUS: PASS

---

## 1E — multi-tenant real
Análise:
- **Eliminação do índice estático `[0]`**: Os serviços de segundo plano `ChiefBacklogLoopService` e `AttemptRecoveryBackgroundService` foram ajustados para iterar a lista completa de perfis/tenants (`profiles.ListAsync()`), eliminando o bug de atender apenas o primeiro tenant cadastrado.
- **Isolamento por Iteração**: Tanto no laço do `ChiefBacklogLoopService` quanto na recuperação de órfãos (`AttemptRecoveryBackgroundService`), a iteração de cada tenant é protegida individualmente com tratamento de exceções. A indisponibilidade de banco de dados ou erro em um tenant não interrompe o processamento dos demais.
- **Habilitação Default do ChiefContextStrategy**: Na classe `GovernanceFeatureSettings.cs`, a flag `ChiefContextStrategyEnabled` foi definida com o padrão `true`.
- **Isolamento de Dados**: Confirmado por testes de integração (`MultiTenantBackgroundLoopTests.cs`), garantindo que solicitações e demandas permaneçam restritas aos limites do seu respectivo `tenantId`.

Findings críticos / altos / médios / baixos:
- Críticos: 0
- Altos: 0
- Médios: 0
- Baixos: 0

STATUS: PASS

---

## CONSOLIDADO
Testes executados / comandos / resultados:
1. `tools/backend/dotnet.sh test tests/Harness.UnitTests/Harness.UnitTests.csproj --filter "FullyQualifiedName~EffortBudget|FullyQualifiedName~LayeredReview|FullyQualifiedName~MastCorrection"`
   - **Resultado**: 15 testes executados, 15 aprovados, 0 falhas (Duração: 22ms).
2. `tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj --filter "FullyQualifiedName~MultiTenant"`
   - **Resultado**: 1 teste executado, 1 aprovado, 0 falhas (Duração: 221ms).
3. `tools/backend/dotnet.sh test tests/Harness.RecoveryTests/Harness.RecoveryTests.csproj --filter "FullyQualifiedName~CheckpointResume"`
   - **Resultado**: 2 testes executados, 2 aprovados, 0 falhas (Duração: 323ms).

CORREÇÕES EXIGIDAS DO CLAUDE (por bloco; vazia apenas se todos PASS):
*(Vazia — Todos os blocos obtiveram STATUS: PASS)*
