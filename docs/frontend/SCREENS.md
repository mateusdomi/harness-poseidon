# SCREENS — Inventário de telas e auditoria de estados (FE-4)

Inventário das 21 telas do frontend: rota, dados consumidos, eventos realtime assinados, ações/comandos e checklist dos 5 estados obrigatórios. Resultado da auditoria tela a tela da FE-4.

**Os 5 estados:** (1) vazio com orientação · (2) skeleton de carregamento · (3) erro com retry · (4) banner de reconexão · (5) permissão negada.

**Notas transversais:**

- **Reconexão (4): GLOBAL** — `ReconnectionBanner` no `AppShell` (`src/app/app-shell.tsx:181`), visível em todas as telas dentro do shell sempre que o realtime não está `connected` (via `useConnectionState`); não é dismissível, some ao reconectar. Onboarding fica fora do shell e não usa realtime. ✅ validado.
- **Permissão negada (5): NÃO SE APLICA no contrato atual** — a camada `src/api/` não modela 401/403 (nenhum `ForbiddenError`; o mock nunca emite 403). Decisão registrada (D-052): quando o backend introduzir autorização, adicionar o conceito no `ApiError`/cliente e o estado nas telas.
- **Erro com retry (3)**: o retry com botão existe para **queries**; erros de **mutation** exibem `role="alert"` e são reenviáveis pela própria ação (form/botão) — padrão consistente em todas as telas.

Legenda: ✅ presente · ➖ não se aplica (justificado)

| # | Tela (rota) | Vazio | Skeleton | Erro+retry | Reconexão | Permissão |
|---|---|---|---|---|---|---|
| 1 | cockpit (`/cockpit`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 2 | projects (`/projects`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 3 | chat (`/chat`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 4 | conversations (`/conversations`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 5 | board (`/board`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 6 | workflows (`/workflows`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 7 | documents (`/documents`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 8 | prototypes (`/prototypes`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 9 | approvals (`/approvals`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 10 | orchestrator (`/orchestrator`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 11 | agents (`/agents`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 12 | tools (`/tools`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 13 | run-project (`/run-project`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 14 | onboarding (`/onboarding`) | ✅ | ✅ | ✅ | ➖ fora do shell, sem realtime | ➖ |
| 15 | organizations (`/organizations`) | ✅ | ✅ | ✅¹ | global ✅ | ➖ |
| 16 | providers (`/providers`) | ✅² | ✅ | ✅ | global ✅ | ➖ |
| 17 | po-assistant (`/po-assistant`) | ✅ | ✅ | ✅³ | global ✅ | ➖ |
| 18 | governance (`/governance`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 19 | licenses (`/licenses`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 20 | notifications (`/notifications`) | ✅ | ✅ | ✅ | global ✅ | ➖ |
| 21 | settings (`/settings`) | ➖⁴ | ✅ | ✅ | global ✅ | ➖ |

¹ Lacuna da auditoria corrigida na FE-4: erro de `workflow-templates` no detalhe da organização agora tem retry (`organization-detail.tsx`).
² Lacuna corrigida na FE-4: estados vazios adicionados para página sem providers, budgets vazios e políticas de roteamento vazias (`providers-page.tsx`).
³ Erros das mutations (análise/criação de demanda) com `role="alert"`, reenviáveis pela ação — padrão do app.
⁴ Tela de formulários sem listas a esvaziar; o card de licença tem fallback "nenhuma licença" + link para `/licenses`.

---

## 1. cockpit — `/cockpit`

- **Dados:** `projects` (seletor de projeto ativo), `tasks`, `approvals`, `agents`, `budgets`, `workflows`, `workflow-runs`, `phases`, `gates`, `audit-events` (`hooks/use-cockpit.ts`).
- **Realtime:** streams `project:<id>` + `global` — `task.created`, `task.stateChanged`, `progress.updated`, `approval.requested`, `approval.resolved`, `gate.changed`, `notification.created`, `agent.statusChanged`, `quota.updated`, `audit.eventAppended`.
- **Ações:** trocar projeto ativo (store); CTA "Executar no chat" (grava `chatDraft` e navega). Sem mutations.
- **Estados:** vazio sem projeto com CTA p/ `/projects`; skeleton; erro com `retryAll` das queries.

## 2. projects — `/projects`

- **Dados:** `projects`, `organizations`.
- **Realtime:** nenhum (lista atualiza por invalidação pós-mutation).
- **Ações:** criar projeto (`api.create('projects')`), editar (`api.update('projects')`). Sem delete na UI.
- **Estados:** vazio com CTA "criar" + busca sem resultados; skeleton; erro com retry (projects + organizations).

## 3. chat — `/chat` (deep-link `?conversation=<id>`)

- **Dados:** `conversations`, `messages`, `models`, `tasks`/`documents`/`agents` (contexto), `projects`.
- **Realtime:** stream `conversation:<id>` — `chat.turnStarted`, `chat.turnChunk`, `chat.turnCompleted`, `chief.turnStateChanged`, `message.appended`.
- **Ações:** nova conversa (`api.create('conversations')`); enviar mensagem (comando `startChatTurn` → `POST /conversations/<id>/turns`).
- **Estados:** vazio sem projeto (CTA) e conversa sem mensagens (orientação + quick actions); skeleton; erro com retry das queries.

## 4. conversations — `/conversations`

- **Dados:** `conversations`, `profiles`, `projects`.
- **Realtime:** nenhum.
- **Ações:** renomear e arquivar/desarquivar (`api.update('conversations')`); abrir conversa → `/chat?conversation=<id>`. Sem delete (contrato).
- **Estados:** vazio com orientação; skeleton; erro com retry.

## 5. board — `/board` (`?state=`, `?task=<id>`)

- **Dados:** `tasks`, `agents`; detalhe: `tasks/<id>`, `task-instructions`, `attempts`, `attempt-events`, `approvals`, `demands`.
- **Realtime:** stream `project:<id>` — `task.created`, `task.stateChanged` (update otimista no cache), `progress.updated`, `approval.requested`, `approval.resolved`; stream `task:<id>` no detalhe.
- **Ações:** `moveTask` (pausar/cancelar/solicitar revisão), `setTaskPriority`, `resolveApproval` (aprovar/reprovar com nota). Humano **não** cria/edita tarefa (contrato).
- **Estados:** vazio sem projeto (CTA), sem tarefas (CTA p/ chat) e vazios do detalhe; skeleton (board + detalhe); erro com retry (board + detalhe).

## 6. workflows — `/workflows`

- **Dados:** `workflows`, `workflow-runs`, `phases`, `gates`, `workflow-templates`, `workflow-versions`, `documents`, `agent-definitions`.
- **Realtime:** streams `project:<id>` + `global` — `gate.changed`, `workflow.versionPublished`, `document.stateChanged`.
- **Ações:** `setWorkflowOperationMode` (com aceite de risco), `publishWorkflowVersion`.
- **Estados:** vazio sem projeto (CTA) e sem workflow (orientação); skeleton; erro com `retryAll`.

## 7. documents — `/documents` (deep-link `?doc=<id>`)

- **Dados:** `documents`, `approvals`, `workflows`, `workflow-versions`; detalhe: `documents/<id>`, `document-versions`.
- **Realtime:** stream `project:<id>` — `document.stateChanged` (update otimista).
- **Ações:** upload (`api.create('documents')`), classificar órfão (`classifyDocument`), solicitar aprovação (`transitionDocument` + `create('approvals')`), aprovar/reprovar (`resolveApproval`).
- **Estados:** vazio sem projeto (CTA), catálogo vazio e detalhe sem conteúdo; skeleton (lista + detalhe); erro com retry (lista + detalhe).

## 8. prototypes — `/prototypes`

- **Dados:** `prototypes`, `visual-references`, `organizations`, `projects`.
- **Realtime:** stream `project:<id>` — `prototype.stateChanged`.
- **Ações:** upload de referência visual (`create('visual-references')`); mudar cenário de prototipação com waiver (`update('projects', { prototyping })`).
- **Estados:** vazio sem projeto, galeria vazia e referências vazias; skeleton; erro com retry (3 queries).

## 9. approvals — `/approvals`

- **Dados:** `approvals`, `projects`, `gates`, `documents`, `tasks` (fila consolidada).
- **Realtime:** streams de todos os projetos — `approval.requested`, `approval.resolved`.
- **Ações:** resolver aprovação (`resolveApproval` — aprovar/rejeitar; nota obrigatória ao reprovar).
- **Estados:** vazio com orientação; skeleton; erro com retry (5 queries).

## 10. orchestrator — `/orchestrator`

- **Dados:** `agents`, `tasks`, `attempts`, `conversations`, `agent-definitions`, `models`, `accounts`, `budgets`; `attempt-events` no detalhe da tentativa.
- **Realtime:** stream `global` + streams das attempts em execução — `agent.statusChanged`, `attempt.started`, `attempt.heartbeat`, `attempt.completed`, `attempt.failed`, `quota.updated`; `chief.turnStateChanged` nas conversas.
- **Ações:** pausar/retomar chefe (`pauseChief`/`resumeChief`), drenar tarefas (`drainChiefTasks`), passagem de bastão (`handoffChief`).
- **Estados:** vazio sem projeto/sem chefe (CTA), grade vazia, tentativa sem eventos; skeleton (página, wizard, attempt-dialog); erro com retry (página, wizard, attempt-dialog).

## 11. agents — `/agents`

- **Dados:** `agents`, `agent-definitions`, `skills`, `tools`, `models`, `tasks`, `attempts`, `audit-events`.
- **Realtime:** stream `global` — `agent.statusChanged`.
- **Ações:** nenhuma (read-only; detalhe em modal).
- **Estados:** vazio sem projeto (CTA), equipe vazia, seções vazias no detalhe; skeleton; erro com retry.

## 12. tools — `/tools`

- **Dados:** `tools`, `skills`, `plugins`, `mcp-servers`.
- **Realtime:** stream `global` — `tool.statusChanged` (skills/plugins/MCP atualizam por invalidação pós-mutation — pendência de contrato).
- **Ações:** habilitar/desabilitar item com confirmação (`api.update(resource, id, { state })`).
- **Estados:** vazio por aba; skeleton; erro com retry; erro da mutation no dialog.

## 13. run-project — `/run-project`

- **Dados:** `run-targets` (por projeto), `projects`.
- **Realtime:** stream `project:<id>` — `run.logAppended` (painel de logs, últimas 200 linhas).
- **Ações:** start/stop/restart por serviço e em lote (`startRunTarget`/`stopRunTarget`/`restartRunTarget`); cleanup do ambiente com confirmação (`cleanupRunEnvironment`).
- **Estados:** vazio sem projeto, sem serviços, log vazio; skeleton; erro com retry.

## 14. onboarding — `/onboarding` (fora do AppShell e do guard de perfil)

- **Dados:** `profiles` (lista + criação), `settings` (preferências do wizard).
- **Realtime:** nenhum.
- **Ações:** criar perfil, persistir tema/idioma/diretório/aceite de modo inseguro (`update('settings')`), selecionar perfil ativo (session store).
- **Estados:** sem perfis → wizard de primeiro uso (orientação por etapas); skeleton; erro com retry (`profilesQuery.refetch`).

## 15. organizations — `/organizations`

- **Dados:** `organizations`, `projects` (contagem/detlhe), `workflow-templates` (detalhe).
- **Realtime:** nenhum.
- **Ações:** criar/editar organização (`create`/`update('organizations')`); busca client-side.
- **Estados:** vazio com CTA "criar", busca sem resultado, seções vazias do detalhe; skeleton (lista + detalhe); erro com retry (lista, projetos e templates do detalhe — corrigido na FE-4).

## 16. providers — `/providers`

- **Dados:** `providers`, `accounts`, `models`, `budgets`, `routing-policies`, `projects`.
- **Realtime:** stream `global` — `quota.updated` (atualiza consumo da conta no cache + invalida budgets).
- **Ações:** sincronizar catálogo (`syncProviderCatalog`); editar política de roteamento com confirmação (`update('routing-policies')`).
- **Estados:** vazio de página/contas/modelos/budgets/roteamento (3 últimos adicionados na FE-4); skeleton; erro com retry (5 queries).

## 17. po-assistant — `/po-assistant`

- **Dados:** `projects` (seletor). Sem query própria (só mutations).
- **Realtime:** nenhum (o `demand.created` emitido na criação é consumido pelo board).
- **Ações:** analisar solicitação (`analyzeSolicitation`), criar demanda estruturada (`create('demands')`); curadoria local dos itens (editar/resolver/descartar).
- **Estados:** vazio sem projeto (orientação) e painéis vazios por tipo; skeleton (projetos) + estado "analisando" no botão; erro de query com retry, erros de mutation com `role="alert"` reenviáveis.

## 18. governance — `/governance`

- **Dados:** `audit-events` + 12 listas de correlação (`projects`, `tasks`, `attempts`, `approvals`, `documents`, `demands`, `solicitations`, `workflows`, `profiles`, `agents`, `models`, `tools`).
- **Realtime:** stream `global` — `audit.eventAppended`.
- **Ações:** nenhuma mutation — filtros client-side e exportação JSON/CSV. Read-only.
- **Estados:** vazio com orientação (contexto de filtros); skeleton; erro com retry (13 queries).

## 19. licenses — `/licenses`

- **Dados:** `licenses` (primeiro item), `entitlements`.
- **Realtime:** nenhum.
- **Ações:** ativação por chave (`activateLicense`, validação Zod do formato).
- **Estados:** sem licença (badge `unlicensed` + formulário de ativação como ação), entitlements vazios; skeleton; erro com retry; erro de ativação detalhado reenviável.

## 20. notifications — `/notifications`

- **Dados:** `profiles/current`, `notifications`, `settings` (preferências).
- **Realtime:** stream `profile:<id>` — `notification.created` (invalida central + badge do shell).
- **Ações:** marcar como lida (item/grupo/todas — `markNotificationsRead`), silenciar (`muteNotifications`), preferências (`update('settings')`).
- **Estados:** vazio com orientação; skeleton; erro com retry.

## 21. settings — `/settings`

- **Dados:** `profiles/current`, `settings`, `diagnostics`, `licenses` (resumo).
- **Realtime:** nenhum.
- **Ações:** `update('settings')` (idioma, tema, diretório, revogação do modo inseguro com confirmação); `createBackup` / `restoreBackup` com confirmação.
- **Estados:** vazio ➖ (formulários; card de licença com fallback + link); skeleton (settings + diagnóstico); erro com retry (settings; diagnóstico via botão de refresh do card).
