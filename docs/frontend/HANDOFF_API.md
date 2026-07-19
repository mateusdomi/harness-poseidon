# HANDOFF — Camada de API do Frontend

Documento vivo do contrato **contract-first** entre o frontend e o backend (.NET ASP.NET Core + SignalR). Cada endpoint/evento que a UI consome está listado com método, rota, tipos request/response, exemplo e a **tela real** que usa (inventário completo em `SCREENS.md`). Atualizado na FE-4; divergências devem ser resolvidas aqui primeiro.

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
| `agent-definitions` | `AgentDefinition` | — | — | — | agents, orchestrator (handoff), workflows |
| `agents` | `Agent` (instância + métricas) | — | — | — | agents, cockpit, board, orchestrator |
| `skills` | `Skill` | — | ✓ | — | tools |
| `tools` | `Tool` | — | ✓ | — | tools |
| `plugins` | `Plugin` | — | ✓ | — | tools |
| `mcp-servers` | `McpServer` | — | ✓ | — | tools |
| `providers` | `Provider` | — | ✓ | — | providers |
| `accounts` | `Account` | — | — | — | providers, orchestrator |
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

### Catálogo de eventos (25)

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
