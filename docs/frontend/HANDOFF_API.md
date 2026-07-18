# HANDOFF — Camada de API do Frontend (FE-0, fatia B)

Documento vivo do contrato **contract-first** entre o frontend e o backend (.NET ASP.NET Core + SignalR). Cada endpoint/evento que a UI consome está listado com método, rota, tipos request/response, exemplo e tela que usa. Será refinado nas próximas fases; divergências devem ser resolvidas aqui primeiro.

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
| `profiles` | `Profile` | — | ✓ | — | onboarding, settings (planejada) |
| `profiles/current` (GET) | `Profile` | — | — | — | shell (badge), onboarding |
| `organizations` | `Organization` | — | — | — | organizations (planejada) |
| `projects` | `Project` | ✓ | ✓ | ✓ | projects, cockpit |
| `conversations` | `Conversation` | ✓ | — | ✓ | conversations, chat |
| `messages` | `Message` | ✓ | — | — | chat, conversations |
| `solicitations` | `Solicitation` (imutável) | ✓ | — | — | po-assistant, cockpit (planejada) |
| `demands` | `Demand` | ✓ | — | — | cockpit, orchestrator (planejada) |
| `tasks` | `Task` | ✓ | — | — | board, cockpit |
| `task-instructions` | `TaskInstruction` (imutável, versionada) | ✓¹ | — | — | board (detalhe da tarefa) |
| `attempts` | `Attempt` | — | — | — | board (evidências), run-project |
| `attempt-events` | `AttemptEvent` | — | — | — | run-project (logs/evidências) |
| `workflow-templates` | `WorkflowTemplate` | — | — | — | workflows |
| `workflow-versions` | `WorkflowVersion` | — | — | — | workflows |
| `workflows` | `Workflow` (modo + aceites de risco) | — | — | — | workflows, governance |
| `workflow-runs` | `WorkflowRun` | — | — | — | workflows, run-project |
| `phases` | `Phase` | — | — | — | workflows, run-project |
| `gates` | `Gate` | — | — | — | governance, approvals |
| `approvals` | `Approval` (reprovação exige nota) | ✓ | — | — | approvals, governance |
| `documents` | `Document` (estados + classificações + waiver) | ✓ | — | ✓ | documents |
| `document-versions` | `DocumentVersion` (imutável) | ✓ | — | — | documents |
| `prototypes` | `Prototype` | ✓ | — | ✓ | prototypes |
| `visual-references` | `VisualReference` | ✓ | — | ✓ | prototypes |
| `agent-definitions` | `AgentDefinition` | — | — | — | agents |
| `agents` | `Agent` (instância + métricas) | — | — | — | agents, cockpit |
| `skills` | `Skill` | — | ✓ | — | tools |
| `tools` | `Tool` | — | ✓ | — | tools |
| `plugins` | `Plugin` | — | ✓ | — | tools |
| `mcp-servers` | `McpServer` | — | ✓ | — | tools |
| `providers` | `Provider` | — | ✓ | — | providers |
| `accounts` | `Account` | — | — | — | providers |
| `models` | `Model` | — | ✓ | — | providers |
| `routing-policies` | `RoutingPolicy` | — | ✓ | — | providers, governance |
| `budgets` | `Budget` | — | ✓ | — | providers, cockpit (custos) |
| `notifications` | `Notification` | ✓ | — | — | notifications, shell (badge) |
| `audit-events` | `AuditEvent` | — | — | — | governance |
| `run-targets` | `RunTarget` | — | — | — | run-project |
| `settings` | `Settings` | — | ✓ | — | settings |
| `licenses` | `License` | — | — | — | licenses |
| `entitlements` | `Entitlement` | — | — | — | licenses, onboarding |

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
| `POST /tasks/<id>/instructions` | `{ body }` | `TaskInstruction` (v+1) | — | board (correção de instrução) |
| `POST /solicitations/<id>/transitions` | `{ state }` | `Solicitation` | — | po-assistant (triagem) |
| `POST /approvals/<id>/resolution` | `{ decision: "approved"\|"rejected", note? }` — **note obrigatória ao reprovar** | `Approval` | `approval.resolved` (+ `gate.changed` se houver gate) | approvals, governance |
| `POST /documents/<id>/transitions` | `{ toState: DocumentState, note? }` | `Document` | `document.stateChanged` | documents |
| `POST /workflows/<id>/operation-mode` | `{ mode, semiautonomousPauseGates?, riskAcceptanceNote }` | `Workflow` | `audit.eventAppended` | workflows, governance |
| `POST /notifications/read` | `{ ids: Ulid[] }` | `number` (alteradas) | — | notifications |
| `POST /notifications/mute` | `{ ids: Ulid[] }` | `number` | — | notifications |
| `POST /conversations/<id>/turns` | `{ content }` | `{ turnId, conversationId }` | `message.appended`, `chat.turnStarted/Chunk/Completed` | chat, po-assistant |

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
| `message.appended` | `{ message }` | `conversation:<id>` | chat, conversations |
| `demand.created` | `{ demand }` | `project:<id>` | cockpit, orchestrator |
| `task.created` | `{ task }` | `project:<id>` | board |
| `task.stateChanged` | `{ taskId, from, to, changedByKind, note? }` | `project:<id>`, `task:<id>` | board |
| `attempt.started` | `{ attempt }` | `task:<id>`, `attempt:<id>` | board, run-project |
| `attempt.heartbeat` | `{ attemptId, taskId, elapsedMs, tokensInput, tokensOutput, costUsd }` | `attempt:<id>`, `task:<id>` | run-project, cockpit |
| `attempt.completed` | `{ attemptId, taskId, durationMs, tokens*, costUsd, commitRefs, summary? }` | `attempt:<id>`, `task:<id>` | board, run-project |
| `attempt.failed` | `{ attemptId, taskId, reason, durationMs, costUsd }` | `attempt:<id>`, `task:<id>` | board, run-project |
| `gate.changed` | `{ gateId, runId, from, to, decidedByProfileId?, note? }` | `project:<id>`, `run:<id>` | governance, workflows |
| `approval.requested` | `{ approval }` | `project:<id>` | approvals |
| `approval.resolved` | `{ approvalId, state, resolvedByProfileId, note? }` | `project:<id>` | approvals, governance |
| `document.stateChanged` | `{ documentId, from, to }` | `project:<id>` | documents |
| `prototype.stateChanged` | `{ prototypeId, from, to }` | `project:<id>` | prototypes |
| `workflow.versionPublished` | `{ templateId, versionId, version }` | `project:<id>` | workflows |
| `notification.created` | `{ notification }` | `profile:<id>` | notifications, shell (badge) |
| `agent.statusChanged` | `{ agentId, from, to, currentTaskId? }` | `global` | agents, cockpit |
| `tool.statusChanged` | `{ toolId, from, to }` | `global` | tools |
| `audit.eventAppended` | `{ auditEvent }` | `global` | governance |
| `run.logAppended` | `{ runId?, attemptId?, line }` | `run:<id>`, `attempt:<id>` | run-project |
| `progress.updated` | `{ taskId, track, value, progress }` — trilhas separadas, nunca somar | `task:<id>` | board, cockpit |
| `quota.updated` | `{ accountId?, budgetId?, usedUsd, limitUsd? }` | `project:<id>`, `global` | providers, cockpit |
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
