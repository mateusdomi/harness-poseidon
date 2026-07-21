# SCREENS — Inventário de telas e auditoria de estados (FR-5)

Inventário das 21 telas do frontend: rota, dados consumidos, eventos realtime assinados, ações/comandos e checklist dos 5 estados obrigatórios. Resultado da auditoria tela a tela da FE-4.

**Os 5 estados:** (1) vazio com orientação · (2) skeleton de carregamento · (3) erro com retry · (4) banner de reconexão · (5) permissão negada.

**Notas transversais:**

- **Reconexão (4): GLOBAL** — `ReconnectionBanner` no `AppShell` (`src/app/app-shell.tsx:181`), visível em todas as telas dentro do shell sempre que o realtime não está `connected` (via `useConnectionState`); não é dismissível, some ao reconectar. Onboarding fica fora do shell e não usa realtime. ✅ validado.
- **Permissão negada (5): GLOBAL** — o OpenAPI real publica 401/403. `QueryCache` converte 403 de leitura em `PermissionDenied` no conteúdo da rota, com texto i18n e retry; 401 invalida a sessão local e retorna ao onboarding. Mutations preservam o erro contextual da própria ação. Onboarding é a superfície pública de recuperação de sessão e fica fora deste estado. ✅ validado por testes unitários.
- **Erro com retry (3)**: o retry com botão existe para **queries**; erros de **mutation** exibem `role="alert"` e são reenviáveis pela própria ação (form/botão) — padrão consistente em todas as telas.

Legenda: ✅ presente · ➖ não se aplica (justificado)

| # | Tela (rota) | Vazio | Skeleton | Erro+retry | Reconexão | Permissão |
|---|---|---|---|---|---|---|
| 1 | cockpit (`/cockpit`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 2 | projects (`/projects`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 3 | chat (`/chat`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 4 | conversations (`/conversations`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 5 | board (`/board`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 6 | workflows (`/workflows`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 7 | documents (`/documents`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 8 | prototypes (`/prototypes`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 9 | approvals (`/approvals`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 10 | orchestrator (`/orchestrator`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 11 | agents (`/agents`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 12 | tools (`/tools`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 13 | run-project (`/run-project`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 14 | onboarding (`/onboarding`) | ✅ | ✅ | ✅ | ➖ fora do shell, sem realtime | ➖ |
| 15 | organizations (`/organizations`) | ✅ | ✅ | ✅¹ | global ✅ | global ✅ |
| 16 | providers (`/providers`) | ✅² | ✅ | ✅ | global ✅ | global ✅ |
| 17 | po-assistant (`/po-assistant`) | ✅ | ✅ | ✅³ | global ✅ | global ✅ |
| 18 | governance (`/governance`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 19 | licenses (`/licenses`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 20 | notifications (`/notifications`) | ✅ | ✅ | ✅ | global ✅ | global ✅ |
| 21 | settings (`/settings`) | ➖⁴ | ✅ | ✅ | global ✅ | global ✅ |

¹ Lacuna da auditoria corrigida na FE-4: erro de `workflow-templates` no detalhe da organização agora tem retry (`organization-detail.tsx`).
² Lacuna corrigida na FE-4: estados vazios adicionados para página sem providers, budgets vazios e políticas de roteamento vazias (`providers-page.tsx`).
³ Erros das mutations (análise/criação de demanda) com `role="alert"`, reenviáveis pela ação — padrão do app.
⁴ Tela de formulários sem listas a esvaziar; o card de licença tem fallback "nenhuma licença" + link para `/licenses`.

---

## 1. cockpit — `/cockpit`

- **Dados:** `projects` (seletor de projeto ativo), `tasks`, `approvals`, `agents`, `budgets`, `workflows`, `workflow-runs`, `phases`, `gates`, `audit-events` (`hooks/use-cockpit.ts`).
- **Realtime:** streams `project:<id>` + `global` — `task.created`, `task.stateChanged`, `progress.updated`, `approval.requested`, `approval.resolved`, `gate.changed`, `notification.created`, `agent.statusChanged`, `quota.updated`, `audit.eventAppended`.
- **Ações:** trocar projeto ativo (store); CTA "Executar no chat" (grava `chatDraft` e navega). Sem mutations.
- **Golden path (2026-07-21):** `GoldenPathChecklist` no topo, alimentado pelo read model canônico `GET /projects/{id}/readiness` (9 etapas, ADR-017); some quando o caminho está completo. Cada etapa expõe status, explicação, CTA única com a **rota do contrato**, bloqueador traduzido do código do backend e selo "Modo simulado" quando `executionMode === 'simulated'`. Antes de existir projeto, as três primeiras etapas vêm de `preProjectSnapshot` (o endpoint responde 404 sem projeto). `readiness.changed` mantém o snapshot vivo.
- **Honestidade:** `SimulatedModeBadge` no cabeçalho quando `VITE_API_MODE` != `http` (a tela mostra cotas/orçamento de fixture). Atividade recente humanizada com ator/horário/objeto/resultado/ícone/link e código cru só em "Ver detalhes".
- **Estados:** vazio sem projeto mantém título "Nenhum projeto ativo" **sem** CTA (o checklist é dono da ação); skeleton; erro com `retryAll` das queries.

## 2. projects — `/projects`

- **Dados:** `projects`, `organizations`.
- **Realtime:** nenhum (lista atualiza por invalidação pós-mutation).
- **Ações:** criar projeto (`api.create('projects')`), editar (`api.update('projects')`). Sem delete na UI.
- **Pré-condição (2026-07-21):** sem organização, a tela explica o vínculo obrigatório e oferece "Criar organização" (`/organizations?new=1&return=project`) — o formulário **não** abre com select vazio. Ao criar a organização, o usuário volta com ela pré-selecionada.
- **Navegação:** `BackLink` "Voltar para projetos" + breadcrumb (desktop) em criação e edição.
- **Formulário:** a sigla (`key`) é derivada do título e para de derivar após edição manual.
- **Estados:** coleção vazia mostra **apenas** o empty state (dono da CTA única; busca/filtros/CTA do topo ficam ocultos); busca sem resultados; skeleton; erro com retry (projects + organizations).

## 3. chat — `/chat` (deep-link `?conversation=<id>`)

- **Dados:** `conversations`, `messages`, `models`, `tasks`/`documents`/`agents` (contexto), `projects`.
- **Realtime:** stream `conversation:<id>` — `chat.turnStarted`, `chat.turnChunk`, `chat.turnCompleted`, `chief.turnStateChanged`, `message.appended`.
- **Ações:** nova conversa (`api.create('conversations')`); enviar mensagem (comando `startChatTurn` → `POST /conversations/<id>/turns`).
- **Jornada inicial (2026-07-21):** sem conversa, o composer já fica pronto — a conversa é criada de forma **idempotente ao enviar** (guarda por ref + envio pendente disparado quando o id vira corrente) — e o estado vazio traz a CTA "Iniciar conversa". O botão "Nova conversa" do cabeçalho só aparece quando já existe conversa (CTA única).
- **Prontidão:** quem decide se o Chief pode executar é o backend (`ExecutionReady` do read model canônico). Faltando provedor/modelo/workflow, `ChatReadinessNotice` lista o que está pronto e o que falta com CTA única; **apenas o envio** é desabilitado, nunca o motivo escondido.
- **Separação visual:** mensagem do usuário → faixa de status do turno ("Turno registrado — o chefe vai executar" / "coordenando", borda tracejada, `role="status"`) → bolha do Chief **somente** com conteúdo real em streaming → erro de envio. Acknowledgement nunca é estilizado como resposta.
- **Estados:** vazio sem projeto (CTA); conversa sem mensagens com orientação + quick actions; bloqueio explicado; skeleton; erro com retry das queries.

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
- **Ações:** `setWorkflowOperationMode` (com aceite de risco), `publishWorkflowVersion`, `linkWorkflowTemplate` (a partir do estado vazio).
- **Estado vazio orientado (2026-07-21):** `WorkflowOnboardingEmpty` explica em uma frase o que é um workflow, recomenda um template publicado real (`recommend-template.ts`), mostra versão e **fases resumidas**, e vincula em um clique ("Usar workflow recomendado"). "Escolher outro template" só aparece havendo outros publicados; sem nenhum, a tela orienta a publicar em vez de recomendar algo inexistente.
- **Estados:** vazio sem projeto (CTA); sem workflow (orientação + recomendação + CTA); erro de vínculo com mensagem; skeleton; erro com `retryAll`.

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

## 10. orchestrator — `/orchestrator` (`?tab=definitions&definition=<id>`)

- **Dados:** `agents`, `tasks`, `attempts`, `conversations`, `agent-definitions`, `skills`, `tools`, `providers`, `models`, `accounts`, `budgets`; `attempt-events` no detalhe da tentativa.
- **Realtime:** stream `global` + streams das attempts em execução — `agent.statusChanged`, `attempt.started`, `attempt.heartbeat`, `attempt.completed`, `attempt.failed`, `quota.updated`; `chief.turnStateChanged` nas conversas.
- **Ações:** pausar/retomar chefe (`pauseChief`/`resumeChief`), drenar tarefas (`drainChiefTasks`), passagem de bastão (`handoffChief`); na aba Definições: criar, visualizar, editar, duplicar, habilitar/desabilitar, arquivar e excluir somente quando nunca utilizada. O lifecycle V3 das definições está reconciliado com o backend real; metadados complementares ainda ausentes seguem no HANDOFF.
- **Prontidão real (2026-07-21):** o card do Chief abre com o estado derivado — `notConfigured`, `awaitingProvider`, `awaitingWorkflow`, `ready`, `running` ou `degraded` — com explicação e CTA correspondente (`Configurar provedor`, `Vincular workflow`, `Revisar agentes`). Precedência fail-closed: degradação e falta de pré-requisito vencem qualquer aparência de prontidão.
- **Honestidade:** modelo herdado do `defaultModelId` da definição (instância sem `modelId`) recebe o rótulo **"Binding pendente"**; `SimulatedModeBadge` marca dados de fixture.
- **Definições sem binding (§17):** definição cujo provider/modelo não resolve exibe **"Binding pendente"** com o impacto ("não pode executar") e CTA para a gestão de modelos — sem inventar conta. Persona, skills e ferramentas continuam legíveis.
- **Formulário guiado de definição (§16):** 6 passos (identidade, papel, comportamento, capacidades, execução, revisão) com descrição/exemplo/impacto por campo; chave técnica derivada do nome, em seção avançada e **bloqueada na edição**; esforço dirigido por `Model.effortMappings` (nível não mapeado desabilitado e limpo); catálogos vazios levam à tela de gestão; template/importação JSON com prévia, erro por campo e rejeição de segredo.
- **Estados:** vazio sem projeto/sem chefe (CTA), grade vazia, tentativa sem eventos, catálogo vazio com CTA de gestão; skeleton (página, wizard, attempt-dialog); erro com retry (página, wizard, attempt-dialog).

## 11. agents — `/agents`

- **Dados:** `agents`, `agent-definitions`, `skills`, `tools`, `models`, `tasks`, `attempts`, `audit-events`.
- **Realtime:** stream `global` — `agent.statusChanged`.
- **Ações:** read-only; detalhe em modal e link profundo para a definição no Orquestrador. Organograma mostra Chief no topo e especialistas agrupados por time, com filtros de time/status, tarefa, saúde/cota, modelo/effort/rota, capacidades, métricas e histórico.
- **Estados:** vazio sem projeto (CTA), equipe vazia, seções vazias no detalhe; skeleton; erro com retry.

## 12. tools — `/tools`

- **Dados:** `tools`, `skills`, `plugins`, `mcp-servers`.
- **Realtime:** stream `global` — `tool.statusChanged` (skills/plugins/MCP atualizam por invalidação pós-mutation — pendência de contrato).
- **Ações:** habilitar/desabilitar item com confirmação (`api.update(resource, id, { state })`).
- **Estados:** vazio por aba; skeleton; erro com retry; erro da mutation no dialog.

## 13. run-project — `/run-project`

- **Dados:** `run-targets` (por projeto), `projects`, `settings` (diretório de dados local).
- **Realtime:** stream `project:<id>` — `run.logAppended` (painel de logs, últimas 200 linhas).
- **Ações:** start/stop/restart por serviço e em lote (`startRunTarget`/`stopRunTarget`/`restartRunTarget`); cleanup do ambiente com confirmação (`cleanupRunEnvironment`); card explica Launcher/atalho, Host/Runner, porta/navegador, estado, diretório de dados, diagnóstico e operação sem IDE.
- **Estados:** vazio sem projeto, sem serviços, log vazio; skeleton; erro com retry.

## 14. onboarding — `/onboarding` (fora do AppShell e do guard de perfil)

- **Dados:** `profiles` (lista + criação), `settings` (preferências do wizard).
- **Realtime:** nenhum.
- **Ações:** criar perfil, persistir tema/idioma/diretório/aceite de modo inseguro (`update('settings')`), selecionar perfil ativo (session store).
- **Estados:** sem perfis → wizard de primeiro uso (orientação por etapas); skeleton; erro com retry (`profilesQuery.refetch`).

## 15. organizations — `/organizations`

- **Dados:** `organizations`, `projects` (contagem/detlhe), `workflow-templates` (detalhe).
- **Realtime:** nenhum.
- **Ações:** criar/editar organização (`create`/`update('organizations')`); busca client-side. Deep link `?new=1` abre a criação; `&return=project` devolve ao fluxo de projeto com a organização pré-selecionada.
- **Formulário (2026-07-21):** "Identificador da URL" derivado do nome, em seção avançada, com helper, validação em tempo real e prévia. **Sem seletor de plano** — read-only na edição, derivado da licença.
- **Marca:** prévia da logo com remoção, color picker + HEX para as duas cores canônicas, tipografia como presets explicados e prévia da marca; URL da logo em modo avançado. Sem upload/crop (ver HANDOFF §9.4). A marca não bloqueia a criação.
- **Detalhe:** cards de workflows e projetos com CTA real (projeto preserva `?org=<id>`); templates e políticas explicam o impacto **sem** CTA, porque não há comando no contrato.
- **Navegação:** `BackLink` "Voltar para organizações" + breadcrumb em criação/edição/detalhe.
- **Estados:** coleção vazia mostra **apenas** o empty state (CTA única); busca sem resultado; seções vazias do detalhe orientadas; skeleton (lista + detalhe); erro com retry (lista, projetos e templates do detalhe — corrigido na FE-4).

## 16. providers — `/providers`

- **Dados:** `providers`, `accounts`, `models`, `budgets`, `routing-policies`, `projects`.
- **Realtime:** stream `global` — `quota.updated` (atualiza consumo da conta no cache + invalida budgets).
- **Ações:** sincronizar catálogo (`syncProviderCatalog`); criar/editar/habilitar/desabilitar/remover conta (remoção somente desabilitada e ainda protegida contra referências); editar política de roteamento com confirmação (`update('routing-policies')`). Contas exibem identidade, plano, autenticação, saúde, cota/janela/reset e capacidades; modelos exibem capabilities, custos, localidade derivada do provider e mapeamento de esforço.
- **Estados:** vazio de página/contas/modelos/budgets/roteamento (3 últimos adicionados na FE-4); skeleton; erro com retry (5 queries).

## 17. po-assistant — `/po-assistant`

- **Dados:** `projects` (seletor). Sem query própria (só mutations).
- **Realtime:** nenhum (o `demand.created` emitido na criação é consumido pelo board).
- **Ações:** analisar solicitação (`analyzeSolicitation`), criar demanda estruturada (`create('demands')`); curadoria local dos itens (editar/resolver/descartar).
- **Estados:** vazio sem projeto (orientação) e painéis vazios por tipo; skeleton (projetos) + estado "analisando" no botão; erro de query com retry, erros de mutation com `role="alert"` reenviáveis.

## 18. governance — `/governance`

- **Dados:** auditoria + correlações; P1 (receipts/métricas, avaliação independente, stale docs, hashline/benchmark, executores e diagnóstico); P2 (learning candidates, evidência, comparação, histórico e métricas).
- **Realtime:** stream `global` — somente o evento canônico `audit.eventAppended`, que invalida auditoria e queries P2. O catálogo 1.1 ainda não publica evento específico de learning.
- **Ações:** exportação JSON/CSV da auditoria; no P2, revisão, solicitação/registro da avaliação independente, shadow validation, aprovação/rejeição, promoção manual confirmada, rollback e depreciação.
- **Estados:** vazio orientado, skeleton, erro com retry, permissão negada, masking e reconexão global. A lista P2 é cursor-paginada; o período atua somente sobre páginas carregadas porque o contrato não possui esse parâmetro.

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
- **Controles de seleção:** o toggle global e os checkboxes por categoria usam o `Checkbox` do design system, cujo estado marcado exibe preenchimento accent + checkmark visível (não depende só de cor). Persistência sem update otimista: a UI reflete o valor do servidor, então uma mutation rejeitada não deixa o controle marcado. Evidência em `evidence/2026-07-20-checkbox-fix/`.

## 21. settings — `/settings`

- **Dados:** `profiles/current`, `settings`, `diagnostics`, `licenses` (resumo).
- **Realtime:** nenhum.
- **Ações:** `update('settings')` (idioma, tema, diretório, revogação do modo inseguro com confirmação); `createBackup` / `restoreBackup` com confirmação.
- **Estados:** vazio ➖ (formulários; card de licença com fallback + link); skeleton (settings + diagnóstico); erro com retry (settings; diagnóstico via botão de refresh do card).

## Regra transversal de término de carregamento

- Skeleton representa somente uma operação realmente em andamento (`isLoading`/fetch ativo), nunca uma query desabilitada por falta de perfil, organização ou projeto.
- Toda tela termina em conteúdo, vazio orientado, onboarding, permissão negada ou erro com retry. Requests GET acima de 4 s exibem aviso redigido e acima de 10 s terminam em erro explícito; escritas longas continuam monitoradas sem aborto automático.
- O gate do pacote self-contained usa limite de 12 s para `.animate-pulse`, percorre todas as rotas e inclui data dir vazio, browser limpo, cookie antigo/inválido, API indisponível, 401/403, reconnect, console e assets.

---

# Roteiro humano de homologação final

Este roteiro complementa os gates automatizados e não declara aceite humano. O homologador registra aprovado/reprovado, evidência e observação por etapa. Execute com backend real, `VITE_API_MODE=http`, navegador limpo e `VITE_GOVERNANCE_CONTRACT_UI=on`.

## Preparação

1. Confirme que o Host responde no endereço configurado e abra o frontend sem cookies/localStorage anteriores.
2. Abra DevTools em Console e Network com “Preserve log”. Ao final de cada bloco confirme zero erro não tratado, zero asset 404 e nenhuma resposta com segredo.
3. Execute em desktop 13" (aprox. 1280×800), tablet (820×1180) e mobile (360×800). Repita os pontos visuais em dark/light, zoom 200% e somente teclado.
4. Em toda tela observe skeleton sem layout quebrado, vazio orientado, erro com retry e, com perfil sem acesso, “Acesso não permitido”. Sessão expirada deve voltar ao onboarding.

## Telas e fluxos

1. **Onboarding:** crie perfil, altere idioma/tema, configure diretório existente e marque o aceite de risco. Reabra em navegador limpo, selecione o perfil e confirme o cockpit.
2. **Organizações:** crie/edite, busque por nome/slug e abra o detalhe; confira projetos e workflows vazios ou relacionados.
3. **Projetos:** crie com organização/membros; edite metadados; verifique busca, arquivados, versão/histórico e confirmação de impacto quando houver execução.
4. **Cockpit:** troque projeto; confira fase, três trilhas separadas, bloqueios/aprovações/custo e atividade 24h/3d/7d. Use “Executar no chat”.
5. **Chat:** crie conversa, envie mensagem e acompanhe início/chunks/fim sem duplicação. Interrompa Host/rede, veja o banner, restaure e envie outra mensagem. Confira painel desktop/drawer mobile.
6. **Conversas:** pesquise, filtre, renomeie, arquive/desarquive e abra por deep link; atualize a página e confira persistência.
7. **Quadro:** combine filtros na URL; abra tarefa por deep link; altere prioridade, pause/cancele/solicite revisão; confira instruções/attempts/aprovações. Exporte o CSV filtrado.
8. **Workflows:** vincule template; crie/edite rascunho e fases; publique, compare versões e tente ações bloqueadas. Troque modo apenas após aceite de risco.
9. **Documentos:** envie `.md`/`.txt`, abra `?doc=`, copie/edite criando versão e compare. Classifique órfão, solicite aprovação e confirme nota na reprovação.
10. **Protótipos:** troque cenário, confirme waiver quando exigido, envie referência e valide fallback de asset.
11. **Aprovações:** filtre por projeto/criticidade/prazo; aprove e reprove com observação; confira atualização nas telas de origem.
12. **Orquestrador:** pause/retome Chief, abra attempts/logs e confirme segredos mascarados. Faça handoff em duas etapas. Na aba existente, percorra o lifecycle das definições respeitando bloqueios.
13. **Agentes:** confira organograma, filtros, estado/cota/modelo/effort; abra detalhe e navegue para persona/definition.
14. **Ferramentas:** percorra Skills/Tools/Plugins/MCP; confira endpoint mascarado e habilite/desabilite com confirmação.
15. **Executar projeto:** confira serviços, stack/porta/URL, logs e ações. Valide Launcher, Host/Runner, diretório, diagnóstico e atalho. Não faça cleanup sobre dados úteis.
16. **Provedores:** sincronize catálogo; crie conta só com referência segura, edite/habilite/desabilite/remova quando permitido. Confira saúde/cota/reset/capabilities, modelos/effort e roteamento.
17. **Licenças:** valide estado, expiração/grace/offline e entitlements; tente chave inválida e ativação válida apenas em ambiente descartável.
18. **Assistente de PO:** envie texto/anexo, revise os cinco painéis, edite/descarte itens e crie demanda; confirme-a no Quadro/Cockpit.
19. **Governança P1/P2:** valide auditoria e painéis P1; na aba Aprendizado, combine projeto/tipo/estado/período, carregue outra página e confira a nota de abrangência do período. Abra detalhe/evidência/comparação e execute, com dados descartáveis: revisão → solicitação de avaliação → avaliação por agente independente → shadow → aprovação. Confirme que nada promove sozinho; marque a confirmação e promova manualmente. Confira métricas, monitoramento e histórico; depois faça rollback e depreciação com justificativa. Repita uma ação sem permissão e valide o 403 contextual; inspecione masking e atualização por `audit.eventAppended`.
20. **Notificações:** marque item/grupo/todas como lidas, silencie e altere preferências; confira badge/realtime.
21. **Configurações:** altere idioma/tema/diretório, revogue modo inseguro, gere backup descartável e confira diagnóstico/licença. Não restaure sobre dados valiosos.

## Encerramento transversal

- Teclado/leitor: skip link, ordem de Tab, Enter/Espaço/Esc, foco preso/retornado, headings, landmarks, labels, `role=status/alert` e nomes de ícones.
- Visual: sem corte ou scroll horizontal indevido, touch targets, foco visível, contraste AA e `prefers-reduced-motion`.
- Dados: paginação 15/30/50, busca `⌘K`/`Ctrl+K`, deep links após refresh, localização, CSV correto e segredo mascarado/redigido.
- Tempo real: snapshot/delta em ordem, sem duplicata, banner durante interrupção, reconexão e atualização posterior sem reload.

Registre o resultado por tela e anexe screenshot/trace ao reprovar. Problema de backend/contrato vai para o HANDOFF; correções permanecem limitadas aos paths do frontend.

## Roteiro de homologação do golden path (2026-07-21)

Objetivo: provar que **um usuário novo, sem conhecimento interno do produto**,
chega da tela vazia até uma execução real do Chief sem explorar menus.

**Ambiente obrigatório:** pacote self-contained **sem `--demo`**, com data dir
**vazio** (`npm run test:e2e:package` prepara exatamente isso). O modo mock
nasce com fixtures e **não** reproduz o estado "zero organização" — o passo 2
não é observável lá.

### A. Jornada guiada (caminho feliz)

1. **Primeira abertura:** o app leva ao onboarding. Crie o perfil (nome, idioma/tema, diretório, aceite de risco). Espere cair no Cockpit.
2. **Cockpit vazio:** confirme o checklist "Comece por aqui" com **1 de 8 concluídos**, "Perfil" riscado e **Organização** como passo atual, com CTA única.
3. **Pré-condição:** vá a Projetos **sem** organização. A tela deve **explicar** o vínculo obrigatório e oferecer "Criar organização" — **nunca** abrir o formulário com um select de organização vazio.
4. **Organização:** confirme que **não existe** seletor de plano e que o "Identificador da URL" **não** aparece no fluxo comum. Preencha só o nome; abra "Opções avançadas" e verifique o identificador derivado + prévia.
5. **Retorno de intenção:** ao salvar, você deve voltar **automaticamente** ao fluxo de projeto com a organização **pré-selecionada**.
6. **Projeto:** confirme que a sigla é derivada do título. Preencha descrição e pessoas e crie.
7. **Chat bloqueado com motivo:** abra o Chat. A área **não** pode estar bloqueada em silêncio: deve aparecer "Execução do chefe bloqueada", a lista do que falta e uma CTA única. O composer fica desabilitado — mas o motivo, visível.
8. **Workflow:** siga a CTA. O estado vazio deve explicar o que é um workflow, mostrar o template **Recomendado** com versão e **fases resumidas**, e vincular em um clique.
9. **Execução liberada:** volte ao Chat. O composer libera e o bloqueio some. **Comece a digitar e envie sem clicar em "Nova conversa"** — a conversa deve ser criada sozinha.
10. **Separação de turno:** durante a execução, confirme que "Turno registrado / coordenando" aparece como **faixa de status** e **não** como bolha de resposta do Chief. A bolha do Chief só surge com conteúdo real.
11. **Orquestrador:** confirme o estado de prontidão explícito e a CTA correspondente; sem execução real, **não** pode dizer "Pronto" sem dependências.

### B. Honestidade operacional

12. **Modo simulado:** rodando sobre fixtures, Cockpit e Orquestrador devem exibir o selo "Modo simulado". Com backend real (`VITE_API_MODE=http`), o selo **não** aparece.
13. **Binding pendente:** se o Chief usa o `defaultModelId` da definição (instância sem `modelId`), o modelo deve vir marcado como "Binding pendente" — nunca como "em uso".
14. **Sem orçamento fictício:** confirme que nenhuma cota/conta/modelo aparece afirmado sem origem real.
15. **Atividade recente:** eventos legíveis ("Tarefa criada" + objeto). O código cru (`task.created`) só pode aparecer dentro de "Ver detalhes".

### C. Regras transversais

16. **CTA única:** em cada tela, nenhuma ação primária pode aparecer duplicada. Verifique Projetos e Organizações **vazios** (só o empty state age) e **com itens** (só o topo age); no Chat sem conversa, "Nova conversa" não deve estar no cabeçalho.
17. **Voltar:** em criação/edição/detalhe, use o botão "Voltar para …" (com seta) e o breadcrumb no desktop — **sem** usar o menu lateral. Confirme retorno à origem correta.
18. **Definições de agente:** crie uma pelo formulário guiado. Confirme os 6 passos, a chave derivada do nome, descrição/exemplo/impacto nos campos e o esforço refletindo o modelo (nível não suportado **desabilitado**). Baixe o modelo JSON, reimporte e confirme a prévia. Importe um arquivo com `apiKey` e confirme a **rejeição com erro no campo**.
19. **Marca:** ajuste cores pelo seletor e pelo HEX, troque a tipografia, veja a prévia e remova a logo. Confirme que a marca **não** bloqueia a criação.
20. **Detalhe da organização:** cards de workflows e projetos levam à tela certa (projeto preserva a organização); templates e políticas explicam o impacto **sem** CTA que não leva a lugar nenhum.
21. **Responsivo/tema/teclado:** repita os passos 3–9 em mobile (360) e desktop, nos temas claro e escuro, com zoom 200% e navegando **apenas** por teclado; confirme foco visível e ordem de leitura.

### D. Critérios de reprovação

- Qualquer tela que **bloqueie sem explicar** o motivo.
- Qualquer afirmação de prontidão, modelo, conta ou orçamento **sem origem real**.
- Duas ações semanticamente idênticas visíveis ao mesmo tempo.
- Formulário que exija decisão técnica (identificador, plano) no fluxo comum.
- Acknowledgement apresentado como resposta inteligente do Chief.
- Console error da aplicação ou asset 404 durante o percurso.
