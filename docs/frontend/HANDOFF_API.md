# HANDOFF — Camada de API do Frontend

Documento vivo do contrato **contract-first** entre o frontend e o backend (.NET ASP.NET Core + SignalR). Cada endpoint/evento que a UI consome está listado com método, rota, tipos request/response, exemplo e a **tela real** que usa (inventário completo em `SCREENS.md`). Atualizado no FR-5; divergências devem ser resolvidas aqui primeiro.

Código-fonte da verdade no frontend: `frontend/src/api/contracts/` (tipos TS + schemas Zod).

## 1. Convenções globais

| Tema | Convenção |
|---|---|
| Base REST | `/api/v1` |
| IDs | ULID string (26 chars Crockford Base32, ex.: `01J9QH5Z3W8K2M4P6R8T0V2X4Y`) |
| JSON | camelCase |
| Paginação | Cursor: `?cursor=&limit=` → `{ "items": [...], "nextCursor": "42" \| null }` |
| Erros | `application/problem+json` (RFC 7807): `{ type, title, status, detail?, errors? }` |
| Datas | UTC ISO-8601 em string (`2026-07-17T12:00:00Z`) |
| Auth | Modo pessoal: sessão local via cookie (`credentials: include`), sem senha no MVP; preparado para OIDC |
| Modos | `VITE_API_MODE=mock` (default, em memória) \| `http` (+ `VITE_API_BASE_URL`). Trocar o modo não exige mexer em componentes |

## 2. Como a UI consome

```ts
// src/api/index.ts — factory
const { api, realtime } = createApi(); // resolve mock/http por VITE_API_MODE

// src/app/api-context.ts — React context provido por AppProviders
const api = useApi();         // ApiClient (REST)
const realtime = useRealtime(); // RealtimeClient (SignalR ou mock)
```

`ApiClient` = CRUD genérico tipado (`list/get/create/update/remove` sobre o `ResourceMap`) + comandos de domínio. **Imutabilidade é contrato**: tarefas, solicitações, demandas, instruções e versões **não têm PATCH** — correção cria nova versão/solicitação; estado muda via comandos (§4).

## 3. Recursos REST

Verbos comuns: `GET /api/v1/<recurso>` (lista, cursor), `GET /api/v1/<recurso>/<id>` (detalhe). Colunas "C"/"U"/"D" indicam `POST` / `PATCH` / `DELETE` adicionais. Todos os schemas Zod estão em `contracts/` (`RESOURCE_SCHEMAS`).

| Recurso (rota) | Tipo TS | C | U | D | Tela que usa |
|---|---|---|---|---|---|
| `profiles` | `Profile` | ✓ | — | — | onboarding (seleção/wizard), conversations (nomes) |
| `profiles/current` (GET) | `Profile` | — | — | — | shell (badge), notifications, settings |
| `organizations` | `Organization` | ✓ | ✓ | — | organizations, projects (form), prototypes |
| `projects` | `Project` | ✓ | ✓ | ✓ | projects, cockpit, seletor de projeto ativo (todas as telas de projeto) |
| `conversations` | `Conversation` | ✓ | ✓ | ✓ | conversations, chat, orchestrator |
| `messages` | `Message` | ✓ | — | — | chat |
| `solicitations` | `Solicitation` (imutável) | ✓ | — | — | po-assistant, governance |
| `demands` | `Demand` | ✓ | — | — | po-assistant (cria), board (detalhe), governance |
| `tasks` | `Task` | ✓ | — | — | board, cockpit, chat, approvals, agents, orchestrator, governance |
| `task-instructions` | `TaskInstruction` (imutável, versionada) | ✓¹ | — | — | board (detalhe da tarefa) |
| `attempts` | `Attempt` | — | — | — | board (evidências), orchestrator, agents |
| `attempt-events` | `AttemptEvent` | — | — | — | board (detalhe), orchestrator (log da tentativa) |
| `workflow-templates` | `WorkflowTemplate` | — | — | — | workflows, organizations (detalhe) |
| `workflow-versions` | `WorkflowVersion` | — | — | — | workflows, documents |
| `workflows` | `Workflow` (modo + aceites de risco) | — | — | — | workflows, cockpit, documents, governance |
| `workflow-runs` | `WorkflowRun` | — | — | — | workflows, cockpit |
| `phases` | `Phase` | — | — | — | workflows, cockpit |
| `gates` | `Gate` | — | — | — | cockpit, workflows, approvals |
| `approvals` | `Approval` (reprovação exige nota) | ✓ | — | — | approvals, governance |
| `documents` | `Document` (estados + classificações + waiver) | ✓ | — | ✓ | documents |
| `document-versions` | `DocumentVersion` (imutável) | ✓ | — | — | documents |
| `prototypes` | `Prototype` | ✓ | — | ✓ | prototypes |
| `visual-references` | `VisualReference` | ✓ | — | ✓ | prototypes |
| `agent-definitions` | `AgentDefinition` | ✓ | ✓ | ✓ | agents, orchestrator (handoff + gestão), workflows |
| `agents` | `Agent` (instância + métricas) | — | — | — | agents, cockpit, board, orchestrator |
| `skills` | `Skill` | — | ✓ | — | tools |
| `tools` | `Tool` | — | ✓ | — | tools |
| `plugins` | `Plugin` | — | ✓ | — | tools |
| `mcp-servers` | `McpServer` | — | ✓ | — | tools |
| `providers` | `Provider` | — | ✓ | — | providers |
| `accounts` | `Account` | ✓ | ✓ | ✓ | providers, orchestrator |
| `models` | `Model` | — | ✓ | — | providers, chat, orchestrator, agents |
| `routing-policies` | `RoutingPolicy` | — | ✓ | — | providers |
| `budgets` | `Budget` | — | ✓ | — | providers, cockpit (custos), orchestrator |
| `notifications` | `Notification` | ✓ | — | — | notifications, shell (badge) |
| `audit-events` | `AuditEvent` | — | — | — | governance |
| `run-targets` | `RunTarget` | — | — | — | run-project |
| `settings` | `Settings` | — | ✓ | — | settings, notifications (preferências), onboarding (wizard) |
| `licenses` | `License` | — | — | — | licenses, settings (resumo) |
| `entitlements` | `Entitlement` | — | — | — | licenses |

¹ Instruções também são criadas via comando `POST /tasks/<id>/instructions` (§4).

### Exemplos

`GET /api/v1/tasks?cursor=14&limit=7&state=development`
```json
{
  "items": [
    {
      "id": "01J9QH5Z3W8K2M4P6R8T0V2X4Y",
      "projectId": "01J9QGZYM7N2P4R6T8W0A2C4E6",
      "demandId": null,
      "title": "Tela de detalhe da tarefa",
      "state": "development",
      "priority": "high",
      "assigneeAgentId": "01J9QH0A1B2C3D4E5F6G7H8J9",
      "blockedReason": null,
      "instructionVersion": 1,
      "progress": { "executed": 55, "validated": 0, "approved": 0 },
      "createdAt": "2026-07-10T14:22:00Z",
      "updatedAt": "2026-07-16T09:05:00Z",
      "dueAt": null
    }
  ],
  "nextCursor": "21"
}
```

Erro (RFC 7807):
```json
{
  "type": "https://httpstatuses.com/404",
  "title": "Recurso não encontrado",
  "status": 404,
  "detail": "tasks/01J... não existe."
}
```

## 4. Comandos de domínio (POSTs fora do CRUD)

Schemas Zod em `contracts/commands.ts`. Todos retornam a entidade afetada e emitem o evento correspondente (§5).

| Método/Rota | Request | Response | Evento(s) | Tela |
|---|---|---|---|---|
| `POST /tasks/<id>/moves` | `{ toState: TaskState, note? }` | `Task` | `task.stateChanged` | board (drag-and-drop) |
| `POST /tasks/<id>/priority` | `{ priority: Priority }` | `Task` | — | board (alterar prioridade — ação humana) |
| `POST /tasks/<id>/instructions` | `{ body }` | `TaskInstruction` (v+1) | — | board (correção de instrução) |
| `POST /solicitations/<id>/transitions` | `{ state }` | `Solicitation` | — | po-assistant (triagem) |
| `POST /approvals/<id>/resolution` | `{ decision: "approved"\|"rejected", note? }` — **note obrigatória ao reprovar** | `Approval` | `approval.resolved` (+ `gate.changed` se houver gate; + `document.stateChanged` se houver documento) | approvals, governance |
| `POST /documents/<id>/transitions` | `{ toState: DocumentState, note? }` | `Document` | `document.stateChanged` | documents |
| `POST /workflows/<id>/operation-mode` | `{ mode, semiautonomousPauseGates?, riskAcceptanceNote }` | `Workflow` | `audit.eventAppended` | workflows, governance |
| `POST /documents/<id>/classification` (FE-2a) | `{ classifications?, phaseName? }` — metadados, não conteúdo | `Document` | — | documents (classificar órfãos) |
| `POST /workflow-templates/<id>/versions` (FE-2a) | `{ phases, gatesByPhase, phaseConfigs?, defaultOperationMode?, transitions?, changelog? }` — versão nasce **publicada** (imutável), número = última + 1 | `WorkflowVersion` | `workflow.versionPublished` | workflows (admin de templates) |
| `POST /notifications/read` | `{ ids: Ulid[] }` | `number` (alteradas) | — | notifications |
| `POST /notifications/mute` | `{ ids: Ulid[] }` | `number` | — | notifications |
| `POST /conversations/<id>/turns` | `{ content }` | `{ turnId, conversationId }` | `message.appended`, `chat.turnStarted/Chunk/Completed` | chat |
| `POST /projects/<id>/chief/pause` (FE-2b) | — | `Project` (state → `paused`) | `agent.statusChanged` (chefe → `waiting`), `audit.eventAppended` (`chief.paused`) | orchestrator |
| `POST /projects/<id>/chief/resume` (FE-2b) | — | `Project` (state → `active`) | `agent.statusChanged` (chefe → `idle`), `audit.eventAppended` (`chief.resumed`) | orchestrator |
| `POST /projects/<id>/chief/handoff` (FE-2b) | `{ targetDefinitionId?, targetModelId?, note }` — **note obrigatória** | `Agent` (nova instância chefe) | `agent.statusChanged` (antigo → `idle`, novo), `audit.eventAppended` (`chief.handedOff`) | orchestrator (passagem de bastão) |
| `POST /projects/<id>/chief/drain` (FE-2b) | `{ note? }` | `number` (tarefas drenadas) | `task.stateChanged` (em andamento → `ready`), `agent.statusChanged`, `audit.eventAppended` (`chief.tasksDrained`) | orchestrator |
| `POST /run-targets/<id>/start` (FE-3) | — | `RunTarget` (state → `running`) | `run.logAppended` (no stream `project:<id>`, `runId`/`attemptId` nulos) | run-project |
| `POST /run-targets/<id>/stop` (FE-3) | — | `RunTarget` (state → `stopped`) | `run.logAppended` (idem) | run-project |
| `POST /run-targets/<id>/restart` (FE-3) | — | `RunTarget` | `run.logAppended` (parada + subida) | run-project |
| `POST /projects/<id>/run-environment/cleanup` (FE-3) | — | `number` (artefatos removidos) | `audit.eventAppended` (`run.environmentCleaned`) | run-project |
| `POST /providers/<id>/sync` (FE-3) | — | `Model[]` (catálogo atualizado) | `quota.updated` (stream `global`), `audit.eventAppended` (`provider.catalogSynced`) | providers |
| `POST /solicitations/analyze` (FE-3) | `{ projectId, text, attachmentNames? }` | `SolicitationAnalysis` (5 painéis de itens `{ id, text }`) | — (a solicitação `kind: request` é criada; a demanda usa o CRUD de `demands` + `demand.created`) | po-assistant |
| `POST /licenses/activation` (FE-3) | `{ key }` — formato `XXXX-XXXX-XXXX-XXXX` | `License` (state → `active`) | `audit.eventAppended` (`license.activated`); 400 em formato inválido | licenses, settings |
| `POST /backups` (FE-3) | — | `BackupHandle` (`{ id, createdAt, sizeBytes }`) | `audit.eventAppended` (`backup.created`) | settings |
| `POST /backups/<id>/restore` (FE-3) | — | — | `audit.eventAppended` (`backup.restored`) | settings |
| `GET /diagnostics` (FE-3) | — | `Diagnostics` (versão, codename, ambiente, contadores) | — | settings |
| `POST /documents/<id>/versions` (FR-3) | `{ body }` — **nova versão por edição manual**; nasce `authorKind: "user"`, `version = currentVersion + 1` | `DocumentVersion` | — (ver pendência FR-3: sugestão `document.versionAdded`) | documents (revisão manual) |
| `POST /tasks/<id>/archive` (FR-3) | — — **só `done`** (409 caso contrário; 409 se já arquivada) | `Task` (`archivedAt` preenchido) | — (ver pendência FR-3) | board (arquivar) |
| `POST /tasks/<id>/unarchive` (FR-3) | — — sempre permitido em tarefa arquivada (409 se não arquivada) | `Task` (`archivedAt: null`) | — | board (desarquivar) |
| `POST /workflow-templates` (FR-4) | `{ name, description? }` — nasce **rascunho** (`state: "draft"`, sem versão) | `WorkflowTemplate` | — (ver pendências FR-4) | workflows (gestão de templates) |
| `POST /workflow-templates/<id>/drafts` (FR-4) | `WorkflowDraftInput` (parcial — sem input, copia a versão vigente) | `WorkflowVersion` (rascunho, `publishedAt: null`) | — | workflows (novo rascunho / editar publicado) |
| `PATCH /workflow-versions/<id>` (FR-4) | `WorkflowDraftInput` — **só rascunho** (409 em publicada/arquivada; única exceção ao "sem PATCH em versões": rascunho é mutável por definição) | `WorkflowVersion` | — | workflows (editor de fases) |
| `POST /workflow-versions/<id>/publish` (FR-4) | `{ changelog? }` — **validação do Harness** (zod + regras, 422 se inválida); publicada = imutável e vira `currentVersionId` | `WorkflowVersion` | `workflow.versionPublished` | workflows (publicar) |
| `POST /workflow-templates/<id>/archive` (FR-4) | — — tombstone, nunca exclusão física | `WorkflowTemplate` | — | workflows |
| `POST /workflow-versions/<id>/archive` (FR-4) | — — tombstone; 409 na **versão vigente** do template | `WorkflowVersion` | — | workflows |
| `DELETE /workflow-versions/<id>` (FR-4) | — — **só rascunho nunca utilizado** (409 em publicada/em uso) | — | — | workflows (excluir rascunho, com confirmação) |
| `DELETE /workflow-templates/<id>` (FR-4) | — — **só template rascunho sem versões publicadas e sem vínculos** (409 caso contrário — arquivar é a alternativa) | — | — | workflows |
| `POST /workflow-templates/<id>/duplicate` (FR-4) | — — novo template rascunho "(cópia)" + rascunho da versão vigente | `WorkflowTemplate` | — | workflows |
| `POST /workflow-versions/<id>/duplicate` (FR-4) | — — novo rascunho no mesmo template (número = última + 1) | `WorkflowVersion` | — | workflows |
| `POST /projects/<id>/workflow` (FR-4) | `{ templateId, versionId? }` — cria o `Workflow` do projeto com a versão publicada vigente (409 se já tem workflow / template sem versão publicada); `operationMode` = `defaultOperationMode` do template (ou `manual`) | `Workflow` | `audit.eventAppended` (`workflow.templateLinked`) | workflows (vincular ao projeto ativo) |
| `POST /agent-definitions` (FR-5/V3) | `AgentDefinitionWriteRequest` | `AgentDefinition` (201) | ledger/outbox do backend | orchestrator |
| `PATCH /agent-definitions/<id>` (FR-5/V3) | write request completo + `expectedVersion` | `AgentDefinition` | ledger/outbox do backend | orchestrator |
| `POST /agent-definitions/<id>/duplicate` (FR-5/V3) | `{ key, name }` | `AgentDefinition` (201) | ledger/outbox do backend | orchestrator |
| `POST /agent-definitions/<id>/<enable\|disable\|archive>` (FR-5/V3) | — | `AgentDefinition` | ledger/outbox do backend | orchestrator |
| `DELETE /agent-definitions/<id>` (FR-5/V3) | — — somente definição customizada nunca utilizada | — (204) | ledger/outbox do backend | orchestrator |

As contas usam o CRUD REST publicado pelo backend: `POST /accounts`, `PATCH /accounts/<id>` e `DELETE /accounts/<id>`. Habilitar/desabilitar é `PATCH` com `{ state: "active" | "disabled" }`, não endpoints de ação. Uma conta nasce desabilitada; `DELETE` só é permitido após desabilitar e continua sujeito a 409 quando houver referência. O `credentialReference` é aceito apenas na criação (`keychain://`, `dpapi://` ou `secret://`) e nunca integra a resposta.

### Campos adicionados na FE-2a

- `Approval.priority: Priority` e `Approval.dueAt: string | null` — criticidade e prazo da decisão; a fila consolidada ordena por prazo → criticidade → mais antigo.
- `Document.phaseName: string | null` — vínculo do documento com a fase do workflow (nome da fase do template). `null` = documento **órfão** (a UI oferece a ação de classificar).
- `WorkflowVersion.phaseConfigs?` (`fase → { documentKinds, progressWeight (0–100), allowedAgentDefinitionIds }`), `WorkflowVersion.defaultOperationMode?`, `WorkflowVersion.transitions?` (`fase → próximas fases permitidas`) — configuração da versão na criação/publicação.
- Resolver aprovação com `documentId` também transiciona o documento (`awaitingApproval` → `approved` | `inElaboration`) e emite `document.stateChanged` (mock já implementa; backend deve espelhar).

### Campos adicionados na FE-2b

- `Agent.modelId: Ulid | null` — override de modelo da instância (preenchido na passagem de bastão); `null` = usa `AgentDefinition.defaultModelId`. Modelo efetivo = `modelId ?? definition.defaultModelId`.
- `Agent.lease: { fencingToken: number, expiresAt: ISO } | null` — concessão de orquestração do chefe (diagnóstico avançado). O fencing token incrementa a cada handoff e invalida escritores antigos; o mock entrega o lease à nova instância e limpa o do chefe anterior.
- Comandos do chefe (tabela acima): pausar/retomar mapeiam em `Project.state` (`paused`/`active`) + estado do agente chefe; handoff cria NOVA instância de agente (definição/modelo opcionais, `note` auditada) e reponta `Project.chiefAgentId`; drain devolve tarefas em andamento (`development|review|corrections|testsGates`) para `ready`, cancela attempts running e põe agentes em `idle`.

### Campos adicionados na FE-3

- `Project.prototyping: { mode: PrototypingMode, waiver: { reason, grantedAt } | null }` — cenário de prototipação do projeto (`externalPrototype | guidelinesOnly | autonomousGeneration | notApplicable`). Waiver **obrigatório** quando `mode = notApplicable` (o schema exige `reason` + `grantedAt` nesse caso).
- `conversations` passou a ter PATCH: `UpdateInputMap.conversations = Partial<Pick<Conversation, "title" | "state">>` — renomear e arquivar/desarquivar pela tela de conversas (o chat também lê `?conversation=<id>` e inclui conversas arquivadas).

### Campos adicionados na FR-3

- `Task.archivedAt: string | null` — **arquivamento é metaestado**, NÃO entra na máquina de estados: a tarefa arquivada mantém `state`/histórico/attempts, some do quadro padrão (filtro "ativas") e permanece acessível pelo filtro "arquivadas" e pelo detalhe. Regra de domínio: arquivar só é permitido para `done` (o contrato não distingue "backlog cancelada" de backlog viva — ver D-073); desarquivar é sempre permitido. Comandos na tabela acima; tarefas continuam **sem PATCH**.
- Comando `saveDocumentVersion` (`POST /documents/<id>/versions`, `{ body }`): edição manual cria versão nova com `authorKind: "user"` e `authorId = profile da sessão` — a origem (agente vs. humano) já era modelada pelo enum `authorKind` de `DocumentVersion` (`user | chief | agent`), nenhum campo novo foi necessário. Verificado na FR-3: `DocumentVersion` **já expõe `body`** (conteúdo da versão) — diff e edição usam o campo existente.

### Campos adicionados na FR-4

- `WorkflowTemplate.state: draft | published | archived` + `archivedAt` — ciclo de vida do template (rascunho editável → publicado → arquivado/tombstone). Template criado do zero nasce `draft`; vira `published` na primeira versão publicada.
- `WorkflowVersion.state: draft | published | archived` + `archivedAt`; `publishedAt` agora é **`string | null`** (nulo enquanto rascunho). Rascunho é editável (única mutação permitida em versões); publicada é imutável — alterar cria NOVA versão via rascunho.
- `WorkflowPhaseConfig` estendido (todos opcionais/aditivos): `objective`, `context`, `acceptanceCriteria[]`, `dependsOn[]` (nomes de fases — sem ciclos, validado na publicação), `entryConditions[]`, `exitConditions[]`, `allowedSkillIds[]` (catálogo `skills`), `allowedToolIds[]` (catálogo `tools`).
- `Project.configHistory: { version, changedAt, changedFields[], summary }[]` — histórico das versões de configuração; o backend/mock registra uma entrada a cada update que altera DE FATO campos versionados (`repositoryUrl`, `repositoryProvider`, `defaultBranch`, `technologies`, `brand`). **Mudança de comportamento (FR-4):** antes o `configVersion` incrementava por presença do campo no payload; agora só incrementa quando o valor muda (deep-compare) — edição de metadados não gera versão.
- **Validação do Harness** (`contracts/workflow-validation.ts`): regras de publicação — ≥1 fase, nomes únicos/não vazios, gates/transições/dependências referenciam fases existentes, sem ciclo simples de dependência, peso 0–100. A ordem das fases é posicional (o array `phases` é a ordem), então "ordem contínua" é garantida pelo modelo. O mock aplica zod + regras em `publishWorkflowDraft` (422); o backend deve espelhar.

### Campos reconciliados/adicionados na FR-5

- `Account`: `identity`, `plan`, `authentication`, `health`, `quotaWindow`, `quotaResetsAt` e `capabilities`, todos reconciliados ao `AccountContract` real. A UI não usa um campo fictício `email`; identidade/e-mail é o campo neutro `identity`.
- `CreateProviderAccountRequest`: `providerId`, `label`, `credentialReference`, cota e metadados opcionais. A referência de segredo é enviada uma única vez e nunca guardada no estado do frontend.
- `Model.effortMappings: { effort, providerValue }[]`, reconciliado ao OpenAPI e exibido no catálogo/rota do agente. O frontend mostra decisão padrão versus override, esforço, motivo disponível, custo estimado e fallbacks sem fabricar telemetria.
- `AgentDefinition` ganhou campos aditivos opcionais no frontend/mock para persona, missão, responsabilidades, instruções, restrições, boas práticas, stacks, esforço, conta preferencial, fallbacks, time, actor/critic, risco, ciclo de vida, versão e histórico.

### Pendências de contrato identificadas na FR-5 (não fabricadas silenciosamente)

- O backend V3 passou a publicar o lifecycle completo e os campos `persona`, `mission`, `operatingPrinciples`, `deliverables`, `qualityCriteria`, `communicationStyle`, `limitations`, `version`, `enabled` e `archivedAt`. O cliente adapta instruções→princípios, responsabilidades→entregáveis, boas práticas→critérios e restrições→limitações. Ainda não há persistência real para time, stacks, effort/account/fallback padrão, actor/critic, risco nem histórico legível de revisões; esses campos permanecem aditivos no mock e não entram como integração completa do bloco.
- O backend já publica `GET /projects/<projectId>/agent-org-chart`, mas a tela atual deriva o mesmo organograma das coleções `agents` + `agent-definitions` para manter compatibilidade com o mock. Uma futura troca para o endpoint agregado não exige mudança visual.
- Não há tipo específico no catálogo canônico para create/update/lifecycle de definição. O backend grava ledger/outbox e a UI invalida queries após a mutation; para atualização multi-janela direcionada, sugere-se `agentDefinition.changed` no stream `global`.

### Gate P1 — contratos integrados e lacunas explícitas

Reconciliação feita em 2026-07-20 contra `docs/contracts/openapi.json` SHA-256 `efcc91b6d557a67afe3266f5ebfa641dbc7c04fff4ae8584e7b5ce5bf97a896d` e `docs/contracts/events.json` SHA-256 `1b860f0adc82e19aa802e1b277f7e7e1e641114666afab7b799d8202a6c3d5db`. A flag continua fail-closed no perfil padrão e é ligada por padrão somente no perfil HTTP de homologação (`npm run dev:real`); `VITE_GOVERNANCE_CONTRACT_UI=off` reverte para a auditoria anterior.

Operações reais consumidas, todas sob `/api/v1/governance-runtime`:

| Operação | Uso no frontend |
|---|---|
| `GET /receipts?projectId=&cursor=&limit=` | lote de receipts; visão geral, Cockpit e lista por turno |
| `GET /receipts/{turnId}` | contrato tipado disponível no cliente; o lote já fornece o detalhe exibido |
| `GET /receipts/{turnId}/metrics` | progressive disclosure do receipt aberto |
| `POST /evaluations` | execução real do fresh-context evaluator; resultado somente da sessão |
| `GET /stale-doc-findings` | findings agrupados por documento, sem exclusão/ação automática |
| `POST /projects/{projectId}/hashline-patches` | patch protegido, após confirmação humana explícita |
| `GET /hashline-benchmark` | comparativo de sucesso, stale rejection, retries e regressões |
| `GET /executors` | disponibilidade/habilitação dos executores reais |

O adapter usa tipos concretos + Zod e valida respostas antes de entregá-las ao React Query. O `MockApiClient` devolve 501 para todas essas operações: não há fixtures P1 no produto, nos E2E HTTP ou na homologação. Drift tests falham se qualquer path ou campo obrigatório consumido desaparecer do OpenAPI.

Lacunas P1 ainda abertas, refletidas como “contrato indisponível” na UI em vez de dados inferidos:

- **Manifest/catálogo completo e linter:** não há endpoint de manifest, status do linter, owner, autoridade, escopo, source of truth, supersedes, dependencies ou enforcement. “Documentos observados” são derivados somente de `receipt.documents` e rotulados assim; não são apresentados como catálogo canônico.
- **Paginação de receipts:** a request aceita cursor, mas a resposta é um array sem next cursor/page envelope. A UI solicita lote de até 100 e informa que não simula próxima página.
- **Findings:** `StaleDocumentFindingContract` não publica severidade, estado, projeto, evidence range ou capabilities. A UI mostra `kind`, detalhe e tarefa recomendada sem inventar criticidade/ação.
- **Evaluations:** existe somente POST. Não há GET de histórico, filtros, replay, estado em andamento nem associação persistida do resultado na API; a UI rotula o retorno como resultado da sessão atual.
- **Chat/Quadro/Agentes/Ferramentas/Documentos/Orquestrador:** não há relações estáveis entre receipts/evaluations e mensagens, gates, definitions, policies, skills/tools/MCP ou documentos de produto. Apenas o Cockpit integra um resumo verificável por `projectId`; as outras relações aguardam contrato.
- **Realtime:** `events.json` segue sem eventos de governança P1. As telas usam fetch/refetch; o banner global de conexão continua honesto, mas não é apresentado como atualização realtime da governança.
- **Diagnóstico/Launcher:** `Diagnostics` publica produto, `apiMode`, `realtimeState`, checks e timestamp. Não publica porta, diretório de dados, migrations, feature flags, Host/Runner separados, restart ou shutdown. A tela Configurações mostra somente os campos reais e a tela Executar projeto mantém as ações de run-target já contratadas.
- **P2:** não há path, schema ou evento para learning candidates, observation/dedup/review/evaluation/shadow/approval/promotion/monitoring/rollback/deprecation. A aba P2 permanece fail-closed e o drift test confirma a ausência; nenhum estado ou endpoint foi antecipado.

Quando o backend publicar qualquer lacuna, a integração deve repetir checksum/reconciliação, tipos/Zod, drift tests, autorização, estados e E2E real antes de ativar a ação correspondente.

### Pendências de contrato identificadas na FR-4 (não fabricadas na UI)

- Ciclo de vida de templates **não tem eventos realtime** próprios (criar/duplicar/arquivar/excluir rascunho) — só `workflow.versionPublished` na publicação. A UI re-sincroniza por invalidação pós-mutation. Sugestão: `workflow.templateChanged` no stream `global`.
- **Troca de template** de um projeto já vinculado não existe — `linkWorkflowTemplate` retorna 409 quando o projeto já tem workflow; trocar exigiria comando próprio (ex.: `POST /workflows/<id>/template`). Hoje só é possível apontar `activeVersionId` dentro do mesmo template (e mesmo assim sem comando — pendência anterior da FE-2a).
- **DELETE de projeto (tombstone)** não é oferecido na UI: `projects` tem DELETE genérico no contrato, mas não há fluxo/tela — arquivamento (`state: archived`) é o caminho suportado e preserva histórico. Se o produto exigir exclusão, definir regras (cascata/tombstone) no contrato primeiro.
- Duplicar template copia apenas a versão VIGENTE como rascunho — histórico de versões antigas não é duplicado (decisão consciente; o histórico permanece no template de origem).
- `Workflow.defaultOperationMode` da versão é aplicado só no VÍNCULO; trocar a versão ativa depois não repropõe modo (o card de modo existente cobre a troca manual com aceite de risco).

### Pendências de contrato identificadas na FR-3 (não fabricadas na UI)

- `Task`/`Demand` **não têm fase** — o filtro "por fase" pedido no item 7.1 NÃO existe na barra do quadro (seria filtro falso). Exigiria campo novo (ex.: `Task.phaseName`, como `Document.phaseName`). A exportação CSV igualmente omite a coluna "fase".
- Arquivamento não tem **evento realtime** próprio — o mock atualiza a tarefa e a UI re-sincroniza por invalidação pós-mutation. Sugestão: emitir `task.archived`/`task.unarchived` (ou incluir `archivedAt` em `task.stateChanged`/um `task.updated`) no stream `project:<id>` para multi-janela.
- `saveDocumentVersion` também não emite evento — sugestão: `document.versionAdded` (`{ documentId, version, authorKind }`) no stream `project:<id>`; hoje a tela invalida o prefixo `documents` após a mutação.
- "Solicitar correção" de documento (item 6.4) é o **mesmo fluxo da reprovação com observação** já existente (`resolveApproval` rejected → documento volta a `inElaboration` no mock) — nenhum comando novo; a observação obrigatória é o pedido de correção.

### Pendências de contrato identificadas na FE-3 (não fabricadas na UI)

- `Conversation` não tem **canal** (web/WhatsApp/etc.) — o filtro por canal pedido pela missão não existe na UI; exigiria campo novo no schema (mesmo precedente da D-038).
- `run-targets` não modela **dependências/ordem de subida** entre serviços — "Iniciar tudo" sobe na ordem da lista; o painel de logs deriva o **nível** (info/erro) do texto da linha, pois `run.logAppended` não tem campo de nível (ver pendência análoga de `AttemptEvent` na FE-2b).
- **Credenciais demo** do ambiente de run não existem no contrato — a UI exibe valores estáticos mascarados via i18n, sem dado real.
- Metadados de referência visual (**briefing de origem**, **momento do fluxo**) não existem em `VisualReference` — a UI usa **tags prefixadas** (`briefing:...`, `fluxo:...`) como convenção; se o backend formalizar, viram campos.
- `Prototype` não tem **histórico de versões** — a galeria mostra só o estado atual; versionamento exigiria recurso novo (ex.: `prototype-versions`, como `document-versions`).
- Reset de budget é **derivado do período** (`monthly` → próximo dia 1) na UI — `Budget` não tem `resetsAt`; se o backend tiver regra própria, expor o campo.

### Pendências de contrato identificadas na FE-2b (não fabricadas na UI)

- `AttemptEvent` não tem campo de **nível/severidade** — o log da tentativa filtra por tipo (`kind`) + busca textual. Se a UX exigir filtro por nível, adicionar `level` a `attemptEventSchema`.
- `AgentDefinition` não versiona **persona/instruções** — a tela de agentes exibe só a `description` atual. Versionamento de persona exigiria recurso novo (ex.: `agent-definition-versions`).
- Ferramentas/skills/plugins/MCP não têm **checksum, permissões, risk tier nem projetos autorizados**; `toolSchema` (kind `mcp`) não referencia o servidor MCP de origem (sugestão: `mcpServerId`). A UI mostra "não disponível no contrato atual".
- Eventos realtime só existem para `tool.statusChanged` — skills/plugins/MCP atualizam por invalidação pós-mutation. Se o backend emitir `skill/plugin/mcpServer.statusChanged`, basta assinar.
- `AuditEvent` não vincula eventos a modelo/ferramenta por outro caminho que não `targetType`/`targetId` — filtros de governança por modelo/ferramenta/tentativa casam apenas quando o `targetType` corresponde.
- Preferências de notificação usam `Settings.notificationsEnabled` + `Settings.mutedCategories` (PATCH `/settings/<id>`) — nenhum recurso novo necessário; não há comando de "desilenciar" item individual.

## 5. Tempo real — hub `/hubs/events`

Hub único, assinatura por streams. Envelope:

```json
{
  "stream": "project:01J9QGZYM7N2P4R6T8W0A2C4E6",
  "sequence": 42,
  "type": "task.stateChanged",
  "occurredAt": "2026-07-17T12:00:00Z",
  "payload": { "taskId": "01J...", "from": "development", "to": "review", "changedByKind": "agent", "note": null }
}
```

**Streams** (`contracts/streams.ts`): `project:<id>`, `conversation:<id>`, `profile:<id>`, `task:<id>`, `attempt:<id>`, `run:<id>`, `global`.

**Re-sync (snapshot + delta)**: `sequence` cresce por stream. A UI usa `SequenceTracker` (`realtime/realtime-client.ts`): `duplicate` → descarta; `gap` → chama `realtime.getSnapshot(stream)` e reaplica (duplicados caem no tracker). `RealtimeClient` expõe `state: connected | reconnecting | disconnected`.

Métodos do hub SignalR (backend): cliente chama `SubscribeToStreams(string[])`, `UnsubscribeFromStreams(string[])`, `GetStreamSnapshot(string)`; servidor emite `event` com o envelope.

### Catálogo de eventos (29)

| Tipo | Payload (resumo) | Stream típico | Tela |
|---|---|---|---|
| `chat.turnStarted` | `{ conversationId, turnId, agentId }` | `conversation:<id>` | chat |
| `chat.turnChunk` | `{ conversationId, turnId, index, text }` | `conversation:<id>` | chat |
| `chat.turnCompleted` | `{ conversationId, turnId, messageId, finishReason }` | `conversation:<id>` | chat |
| `message.appended` | `{ message }` | `conversation:<id>` | chat |
| `demand.created` | `{ demand }` | `project:<id>` | board, cockpit (origem: po-assistant/chefe) |
| `task.created` | `{ task }` | `project:<id>` | board, cockpit |
| `task.stateChanged` | `{ taskId, from, to, changedByKind, note? }` | `project:<id>`, `task:<id>` | board |
| `attempt.started` | `{ attempt }` | `task:<id>`, `attempt:<id>` | board, orchestrator |
| `attempt.heartbeat` | `{ attemptId, taskId, elapsedMs, tokensInput, tokensOutput, costUsd }` | `attempt:<id>`, `task:<id>` | orchestrator, cockpit |
| `attempt.completed` | `{ attemptId, taskId, durationMs, tokens*, costUsd, commitRefs, summary? }` | `attempt:<id>`, `task:<id>` | board, orchestrator |
| `attempt.failed` | `{ attemptId, taskId, reason, durationMs, costUsd }` | `attempt:<id>`, `task:<id>` | board, orchestrator |
| `gate.changed` | `{ gateId, runId, from, to, decidedByProfileId?, note? }` | `project:<id>`, `run:<id>` | cockpit, workflows |
| `approval.requested` | `{ approval }` | `project:<id>` | approvals, board, cockpit |
| `approval.resolved` | `{ approvalId, state, resolvedByProfileId, note? }` | `project:<id>` | approvals, board, cockpit |
| `document.stateChanged` | `{ documentId, from, to }` | `project:<id>` | documents, workflows |
| `prototype.stateChanged` | `{ prototypeId, from, to }` | `project:<id>` | prototypes |
| `workflow.versionPublished` | `{ templateId, versionId, version }` | `project:<id>` (projetos que usam o template), `global` | workflows |
| `notification.created` | `{ notification }` | `profile:<id>` | notifications, shell (badge) |
| `agent.statusChanged` | `{ agentId, from, to, currentTaskId? }` | `global` | agents, cockpit, orchestrator |
| `tool.statusChanged` | `{ toolId, from, to }` | `global` | tools |
| `audit.eventAppended` | `{ auditEvent }` | `global` | governance, cockpit |
| `run.logAppended` | `{ runId?, attemptId?, line }` | `run:<id>`, `attempt:<id>` | run-project |
| `progress.updated` | `{ taskId, track, value, progress }` — trilhas separadas, nunca somar | `task:<id>` | board, cockpit |
| `quota.updated` | `{ accountId?, budgetId?, usedUsd, limitUsd? }` | `project:<id>`, `global` | providers, cockpit, orchestrator |
| `chief.turnStateChanged` | `{ conversationId, turnId?, state }` | `conversation:<id>` | chat, orchestrator |
| `decision.requested` | `{ decisionId, projectId, title, reason, requestedByAgentId? }` | `project:<id>` | approvals, cockpit |
| `decision.resolved` | `{ decisionId, outcome, resolvedByProfileId, note? }` | `project:<id>` | approvals, cockpit |
| `project.created` | `{ project }` | `global` | projects, shell |
| `prototype.created` | `{ prototype }` | `project:<id>` | prototypes |

## 6. Semântica de domínio refletida no contrato

- **Chefe** coordena o projeto (`Project.chiefAgentId`), conversa com o usuário e delega; **humanos criam solicitações/intervenções; o chefe cria demandas e tarefas** — humano não cria/edita tarefa técnica (sem rota pública de update em `tasks`).
- **Instruções imutáveis versionadas**: `task-instructions` sem PUT; correção = nova versão (`instructionVersion` na tarefa). Documentos idem (`document-versions`).
- **Três trilhas de progresso sempre separadas**: `progress: { executed, validated, approved }` (0–100 cada) — nunca somar.
- **Quadro**: `TaskState = backlog | ready | development | review | corrections | testsGates | blocked | done`.
- **Documentos**: `planned | inElaboration | inReview | awaitingApproval | approved | outdated | superseded | notApplicable` + `inconsistent` + `waiver`.
- **Agentes**: `working | idle | waiting | error | outOfQuota`.
- **Modos de operação**: `manual | semiautonomous | autonomous`; troca exige `riskAcceptanceNote` registrada em `Workflow.riskAcceptances`; semiautônomo define `semiautonomousPauseGates`.
- **Notificações**: severidade, categoria, `groupKey` + `dedupeCount` (agrupamento/deduplicação), `status: unread | read | muted`.
- **Licença**: `state`, `expiresAt`, `gracePeriodEndsAt`, `offlineMode`, dispositivo; `entitlements` com `limit`.

## 7. Modo mock e testes

- `MockApiClient` (`client/mock-client.ts`): store em memória das fixtures, latência 100–600 ms, erros por cenário (`queueError(problem)` / `failureRate`). **Mutações emitem eventos no `MockRealtimeClient`** — a UI mockada é viva, sem refetch.
- `MockRealtimeClient` (`realtime/mock-client.ts`): sequences por stream, múltiplos subscribers, log por stream para snapshot, heartbeat de attempts em andamento, chat por chunks, `replay()`/`emitOutOfOrder()` para exercitar dedupe e lacuna.
- Fixtures determinísticas (seed 42, mulberry32 + ULID determinístico) em `fixtures/`: 1 perfil, 2 organizações, 2 projetos, 40 tarefas nas 8 colunas, attempts com evidências, conversas/mensagens, documentos em 8 estados (+ versões e waiver), workflow template/versão/run com fases e gates, 5 aprovações, 7 agentes, skills/tools/plugins/MCP, providers/contas/modelos/budgets/roteamento, 10 notificações, auditoria, run-targets, settings, licença/entitlements, protótipos e referências visuais.
- **msw**: `src/api/mocks/` (handlers + worker). Em dev, `VITE_MSW=on` serve as fixtures via HTTP `/api/v1` para inspeção no navegador. Testes não usam rede.
- **Gatilho de cenário `[plan]`** (mock, E2E): mensagem de chat contendo `[plan]` faz o `MockApiClient` simular o planejamento do chefe após o turno — cria demanda + 2 tarefas (títulos fixos), move uma tarefa backlog → ready → development via eventos `task.stateChanged` reais e abre uma aprovação de gate pendente. Usado pelo gate E2E da FE-1; não é contrato de backend.

## 8. Paginação e assets estáticos (FR-1)

- **Paginação client-side (D-063):** as telas de coleção paginam sobre os itens já carregados (`api.list(...).items`), via hook/componente compartilhados (`src/features/shared/hooks/use-pagination.ts` + `components/pagination.tsx`; padrão 15/página, opções 15/30/50). O contrato **cursor** (`?cursor=&limit=`) da camada api NÃO mudou e nenhum hook de dados passou a consumi-lo — a migração para paginação server-side, quando o volume real exigir, é localizada nos hooks de dados. Chat, logs e realtime seguem incrementais (cursor), sem paginação. Em Documentos, `?page=`/`?pageSize=` vão para a URL (preservando `?doc=`); nas demais telas o estado é local (documentado em DECISIONS.md).
- **Assets de referência visual (D-066):** as fixtures de `visual-references` apontam para caminhos locais `/refs/*.png`, servidos de `frontend/public/refs/` (gerados por `frontend/scripts/generate-ref-assets.mjs`, sem dependências). O campo `VisualReference.imageUrl` aceita URL absoluta (http) ou caminho local servido pelo próprio frontend; o componente tem fallback `onError` para asset indisponível.
