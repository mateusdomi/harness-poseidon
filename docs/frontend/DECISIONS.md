# DECISIONS — Frontend Harness Poseidon

Registro de decisões de engenharia e suposições não bloqueadoras, conforme o prompt de missão v1.1.

## D-001 — Monorepo único
- **Decisão:** o frontend vive em `frontend/` dentro do repositório `harness-poseidon`, sem `git init` próprio.
- **Justificativa:** exigência explícita da seção 0 do prompt de missão.

## D-002 — Branches permanentes: apenas `main` e `develop`
- **Decisão:** todo o desenvolvimento ocorre em `develop`; `main` só recebe merge com autorização humana explícita. Nenhuma branch adicional será criada.
- **Justificativa:** convenção Git obrigatória da seção 0.

## D-003 — Nome de produto
- **Decisão:** `product.name` = "Poseidon" (mock inicial); "Harness" permanece como codinome técnico.
- **Justificativa:** seção 5 do prompt de missão.

## D-004 — Bootstrap do repositório vazio
- **Decisão:** o remote estava vazio; criado commit mínimo de bootstrap em `main` (README, .gitignore), `main` e `develop` publicadas, desenvolvimento iniciado em `develop`.
- **Justificativa:** procedimento obrigatório da seção 0 para remoto vazio.

## D-005 — CRUD genérico tipado + comandos de domínio (ApiClient)
- **Decisão:** o `ApiClient` expõe verbos genéricos (`list/get/create/update/remove`) tipados por um `ResourceMap` central (37 recursos, chave = rota plural kebab-case), mais comandos de domínio explícitos (`moveTask`, `resolveApproval`, `startChatTurn`, ...). Recursos imutáveis (tarefas, solicitações, demandas, instruções, versões) ficam fora do `UpdateInputMap` — a imutabilidade é garantida em tempo de compilação, não por convenção.
- **Justificativa:** contract-first sem boilerplate de 37 sub-APIs; trocar mock ↔ http não mexe em componentes.

## D-006 — Re-sync realtime: snapshot + delta com SequenceTracker
- **Decisão:** `sequence` crescente por stream; o consumidor usa `SequenceTracker` (dedupe + detecção de lacuna). Em lacuna ou reconexão, pede `realtime.getSnapshot(stream)` e reaplica — o tracker descarta duplicados. Evento com lacuna não é aplicado até o re-sync.
- **Justificativa:** envelope exigido pela missão; mecanismo único e testável para dedupe/lacuna (provado em `mock-realtime.test.ts`).

## D-007 — Fixtures determinísticas com PRNG próprio (mulberry32 + ULID determinístico)
- **Decisão:** seed fixa 42; ULIDs gerados por `DeterministicUlidGenerator` (relógio base monotônico + contador, entropia do PRNG). O app cria um gerador separado (seed+1000) para entidades novas em runtime, sem colidir com as fixtures.
- **Justificativa:** fixtures estáveis para testes de round-trip e reprodução de cenários sem dependência externa.

## D-008 — msw opcional em dev; app usa MockApiClient em memória
- **Decisão:** no modo `mock` o app consome o `MockApiClient` direto (sem rede). O worker msw (`VITE_MSW=on`) serve as mesmas fixtures via HTTP `/api/v1` apenas para inspeção de tráfego no navegador. Testes nunca passam por rede.
- **Justificativa:** mock em memória emite eventos realtime nas mutações (sistema vivo); msw por HTTP não teria como empurrar eventos para o `MockRealtimeClient` sem acoplamento extra.

## D-009 — Evolução dos contratos para FE-1a (marca, criticidade, repositório, settings)
- **Decisão:** os contratos de `organizations`, `projects` e `settings` foram estendidos no próprio `src/api/contracts` (campos novos obrigatórios com defaults no mock/fixtures): `brand` (logo/cores/tipografia, `null` = herda), `defaultWorkflowTemplateIds`, `templateKeys`, `policies` na organização; `state`, `criticality` (reusa o enum `priority`), `repositoryProvider`, `defaultBranch`, `technologies`, `brand`, `memberProfileIds`, `configVersion`, `lastActivityAt` no projeto; `workingDirectory` e `unsafeModeAcceptedAt` no settings. `profiles` e `organizations` entraram no `CreateInputMap`; `organizations` no `UpdateInputMap`; `projects.update` ganhou os campos novos. O mock incrementa `configVersion` quando campos versionados (repositório, tecnologias, marca) mudam.
- **Justificativa:** a camada de API era o contrato "completo" da FE-0, mas o domínio desta fatia (marca herdável, abas de projeto, aceite de modo inseguro) não existia nela. Estender schema + mock + fixtures mantém contract-first e round-trip verde (89 testes de API inalterados e passando).

## D-010 — Gate de onboarding via sessão local (zustand persistida) + rota fora do shell
- **Decisão:** `src/stores/session-store.ts` (`poseidon-session` no localStorage) guarda `activeProfileId`. O componente `RequireProfile` envolve a rota do AppShell no router: sem perfil ativo, tudo redireciona para `/onboarding` (com `state.from` para voltar). `/onboarding` é rota standalone (tela cheia, sem AppShell) e fora do guard. A página decide: 0 perfis → wizard; ≥1 perfil → seleção de perfil (criar novo abre o wizard).
- **Justificativa:** modo pessoal com perfis locais exige um gate simples e testável; manter o onboarding fora do shell evita navegação chrome em primeiro uso.

## D-011 — Aceite do modo inseguro persistido em `settings.unsafeModeAcceptedAt`
- **Decisão:** no mock o sandbox é considerado indisponível; o wizard exige checkbox explícito (`z.literal(true)`) e persiste o timestamp via `update('settings', …)`. O mock cria settings padrão junto com todo perfil novo (`#build('profiles')`), e o wizard atualiza tema/idioma/diretório/aceite em seguida. Exibição permanente do aceite na tela de Configurações fica para a fatia da feature settings (FE-1b/c).
- **Justificativa:** o aceite é dado de perfil, não de sessão — settings é o recurso por-perfil já existente.

## D-012 — Resolver zod próprio em vez de `@hookform/resolvers`
- **Decisão:** `src/lib/form.ts` implementa um resolver minimalista (safeParse → erros aninhados por path). Mensagens de validação são CHAVES i18n (ex.: `common.validation.required`), traduzidas na renderização.
- **Justificativa:** `@hookform/resolvers` não era dependência do projeto; o resolver próprio tem ~40 linhas, sem nova dependência, e padroniza mensagens como chaves de catálogo.

## D-013 — Navegação intra-feature por estado de view (sem sub-rotas nesta fatia)
- **Decisão:** detalhe/criação/edição de organizações e projetos são estados de view dentro da página (`list | detail | create | edit`), sem novas rotas. Busca e filtros (organização, criticidade, estado) são client-side sobre a lista completa. Listas atualizam por invalidação do React Query após mutations (o catálogo de eventos realtime não tem eventos de projeto/organização/perfil — "evento/invalidação": aqui se aplica invalidação).
- **Justificativa:** simplicidade mobile-first e escopo da fatia; deep-linking (`/projects/:id`) e filtros server-side entram quando o backend real existir.

## D-014 — Projeto ativo em zustand (sessionStorage) + rascunho de chat na mesma store
- **Decisão:** `src/stores/active-project-store.ts` guarda `activeProjectId` (persistido em **sessionStorage** via `partialize` — sobrevive a reload, morre ao fechar a aba) e `chatDraft` (não persistido). O hook compartilhado `useActiveProject` (`src/features/shared/hooks/use-active-project.ts`) resolve o projeto efetivo — seleção persistida ou o primeiro da lista, que é adotado e persistido automaticamente — e é usado por cockpit e chat. O "Executar no chat" do cockpit escreve `chatDraft` e navega para `/chat`; o composer consome e limpa o rascunho.
- **Justificativa:** a missão pede persistência "na sessão/zustand"; sessionStorage (em vez de localStorage) respeita o escopo de sessão. Uma única store evita sincronização entre cockpit/chat/quadro (FE-1c).

## D-015 — Hook `useRealtimeStream` único para assinaturas + invalidação React Query
- **Decisão:** `src/features/shared/hooks/use-realtime-stream.ts` encapsula: subscribe com cleanup no unmount, dedupe/lacuna por `SequenceTracker` (lacuna → `getSnapshot` + revalidação), filtro por tipos (`types`), callback por evento (`onEvent`) e invalidação declarativa de query keys (`invalidate` + `invalidateEvents` para invalidar só em tipos específicos — ex.: chat só refaz `messages` em `message.appended`/`chat.turnCompleted`, nunca a cada chunk). Callbacks vivem em ref para não recriar a assinatura a cada render. Cockpit assina `project:<id>` + `global` (agentes/quota/auditoria chegam pelo stream global no contrato); chat assina `conversation:<id>`.
- **Justificativa:** um único ponto testável para o protocolo snapshot+delta (D-006), sem espalhar `SequenceTracker` pelas telas.

## D-016 — react-markdown sem syntax highlighter; código destacado por tokens do DS
- **Decisão:** mensagens renderizam com `react-markdown` (v10, nova dependência registrada em package.json). O "destaque de código" é visual via tokens do design system (bloco `pre` em `bg-surface-elevated` + `font-mono` + borda; inline como chip mono) — sem `react-syntax-highlighter`/highlight.js/shiki (~200–600 kB). Links abrem em nova aba com `rel="noreferrer"`.
- **Justificativa:** bundle enxuto e zero cor hardcoded; highlight sintático real pode entrar depois (ex.: shiki em worker) sem mudar a API do componente (`MarkdownContent`).

## D-017 — Derivações do cockpit como funções puras testadas (trilhas, fase, próxima ação, cotas)
- **Decisão:** `src/features/cockpit/lib/cockpit-derive.ts` concentra a lógica: (a) progresso = **média por trilha** das tarefas (executado/validado/aprovado sempre separados — nunca somados); (b) fase atual = ativa, senão primeira pendente; progresso da fase agrega as colunas do domínio dela via mapa explícito `PHASE_TASK_STATES` (Planejamento→backlog/ready, Execução→development, Validação→review/corrections/testsGates, Publicação→done — chaves são os NOMES das fases vindos do template, dado e não texto de UI); (c) próxima ação = regras em prioridade fixa (aprovações pendentes → bloqueios → agentes em erro/sem cota → cotas → resumo da fase), retornando CHAVE i18n (label e mensagem do chat vivem no catálogo, respeitando o idioma ativo); (d) severidade de budget por `alertThresholdPct` (warning) e 100% (critical).
- **Justificativa:** contratos não ligam tarefas a fases — o mapa explícito é a interpretação mais simples e documentada; regras puras são testáveis sem render.

## D-018 — Modelo/esforço e ações rápidas do chat: UI derivada de contexto, sem mudança de contrato
- **Decisão:** o seletor de modelo lista `models` habilitados das fixtures (opção vazia = "padrão do chefe"); esforço é baixo/médio/alto (chaves i18n, não enum do domínio). Ambos são estado local do composer — **não são enviados** ao `startChatTurn` (o contrato `StartChatTurnInput` só aceita `content`); quando o backend suportar, basta incluir no payload. Ações rápidas são derivadas do contexto do projeto (`deriveQuickActions`: resumo sempre; bloqueios/aprovações quando > 0; planejar demanda sempre) e enviam a mensagem estruturada correspondente do catálogo i18n. Referências cruzadas: citações `"…"` no conteúdo casadas com títulos de tarefas/documentos viram chips para `/board?task=<id>` e `/documents?doc=<id>` (query params preparados para FE-1c). Anexos são upload simulado no cliente (progresso incremental local); os nomes entram como lista markdown no fim da mensagem.
- **Justificativa:** zero mudança de contrato nesta fatia; comportamento final idêntico quando o backend existir.

## D-019 — Banner de reconexão global no AppShell (não por tela)
- **Decisão:** `ReconnectionBanner` (`src/features/shared/components/`) é renderizado uma única vez no AppShell, logo abaixo do header — cobre todas as telas. Lê `realtime.state` via `useConnectionState` (reativo a `onStateChange`); some sozinho ao voltar para `connected` (não dismissível). `role="status"` com tom `warning` (reconectando) ou `error` (desconectado).
- **Justificativa:** a forma mais limpa pedida pela missão: um único ponto, sem duplicar em cada página; o componente segue reutilizável/testável isoladamente.

## D-020 — Comando `setTaskPriority`: prioridade muda por comando, não por PATCH
- **Decisão:** a ação humana "alterar prioridade" virou um comando de domínio novo — `setTaskPriorityInputSchema` em `contracts/commands.ts`, `POST /tasks/<id>/priority` no `ApiClient` (mock + http). Tarefa continua fora do `UpdateInputMap` (imutabilidade de conteúdo preservada). Não há evento `task.priorityChanged` no catálogo: a UI invalida as queries do quadro no sucesso da mutation; quando o backend modelar o evento, basta incluí-lo nos tipos assinados.
- **Justificativa:** a missão exige a ação e o contrato não a cobria; comando explícito segue o padrão já estabelecido (D-005) em vez de abrir PATCH em tarefa.

## D-021 — Pausar/cancelar/solicitar revisão mapeados em `moveTask` (sem novos estados)
- **Decisão:** o enum de colunas tem 8 estados e nenhum deles é "pausada"/"cancelada". O mapeamento adotado no detalhe da tarefa: **pausar** → `moveTask('blocked')` com nota de pausa (bloqueio manual com motivo visível no card e no cockpit); **cancelar** → `moveTask('backlog')` com nota de cancelamento (sai do fluxo ativo; o chefe replaneja); **solicitar revisão** → `moveTask('review')` com nota. As notas são o rastro de auditoria da intenção. Se o backend criar estados/comandos próprios (`paused`, `cancelled`), só o `TaskActions` muda.
- **Justificativa:** zero mudança de máquina de estados nesta fatia (adicionar estados quebraria cockpit, fixtures e o contrato das 8 colunas); o comportamento é honesto — pausa aparece como bloqueio, que é exatamente o efeito operacional de pausar.

## D-022 — Detalhe da tarefa: drawer (lg+) vs página dedicada (mobile) via matchMedia
- **Decisão:** `?task=<id>` controla a abertura (deep-linkável). A escolha drawer × página é por `useMediaQuery('(min-width: 1024px)')` (hook novo em `features/board/hooks`), não por CSS `hidden lg:block` — assim só UMA instância do detalhe existe no DOM (sem duplicação de regiões/heading para leitores de tela). O drawer é `role="dialog"` modal com foco preso (Tab faz loop), Esc fecha, backdrop fecha e o foco retorna a quem abriu. Em jsdom (sem matchMedia) o padrão é mobile; testes do drawer stubam `window.matchMedia`.
- **Justificativa:** a11y real (foco preso + Esc) exigida pela missão; matchMedia evita dois dialogs no accessibility tree.

## D-023 — Movimento em tempo real: atualização otimista do cache + destaque discreto
- **Decisão:** `useBoardRealtime` assina `project:<id>`. Em `task.stateChanged`, aplica `setQueryData` na lista do quadro (movimento imediato) e marca o card em `recentlyMoved` (1,5 s) — o card recebe um flash de `ring-info` com transição de 200 ms via `motion-safe:` (desligado com `prefers-reduced-motion`). Todos os eventos do quadro também invalidam o prefixo `['board']` (re-sync autoritativo); o detalhe aberto assina `task:<id>` com a mesma invalidação. Tempos relativos ("há 2 min") usam um `useNow` único por página (intervalo de 30 s) passado aos cards.
- **Justificativa:** `setQueryData` dá o movimento instantâneo pedido; a invalidação por prefixo cobre criação de tarefas, progresso e aprovações sem lógica por evento. Um timer global evita 40 intervals (um por card).

## D-024 — Objetivo/critérios de aceite: contrato atual não modela; detalhe usa demanda + instrução + trilhas
- **Decisão:** `Task` não tem campos de objetivo nem critérios de aceite. O detalhe mostra, com dados reais do contrato: **demanda de origem** (`demandId` → título/descrição da demanda), **instrução ao agente** (imutável, com seletor de versões anteriores, somente leitura) e **progresso nas 3 trilhas** (o modelo de aceite do domínio). Tentativas exibem timeline de eventos, custo, tokens, duração, commits/diffs e motivo de falha. Campos dedicados de objetivo/critérios ficam como pendência de contrato para o backend (FE-2).
- **Justificativa:** regra inegociável de zero texto/dado inventado — melhor omitir a seção do que simular conteúdo que o contrato não tem.

## D-025 — Gatilho determinístico `[plan]` no MockApiClient para o gate E2E
- **Decisão:** mensagem de chat contendo `[plan]` (constantes exportadas: `CHIEF_PLAN_TRIGGER`, `CHIEF_PLAN_TASK_A/B`, `CHIEF_PLAN_APPROVAL_TITLE`) faz o mock simular o planejamento do chefe após o turno: cria demanda + 2 tarefas (títulos fixos), move a tarefa A backlog → ready → development (intervalos fixos de 700 ms, eventos `task.stateChanged` reais no stream do projeto) e abre uma aprovação de gate pendente ligada à tarefa. Reusa os métodos públicos (`create`/`moveTask`) — nenhum caminho especial de dados, os eventos são os mesmos das mutações normais. Não há UI para o gatilho; só o E2E o usa.
- **Justificativa:** o gate FE-1 precisa de "chefe cria demanda/tarefas (simulado)" determinístico e dirigível pela UI (o chat já é a porta de entrada do domínio). Um comando novo de API seria contrato falso; um token de mensagem é cenário de mock puro.

## D-026 — Gate E2E contra `vite preview` do build (webServer: `build && preview`)
- **Decisão:** o Playwright sobe `npm run build && npm run preview` (porta 4173, `reuseExistingServer` fora de CI) — já era a configuração existente e se mostrou estável; mantida. Specs: `e2e/fe1-flow.spec.ts` (fluxo completo do gate) e `e2e/app-shell.spec.ts` (smoke, ajustado para completar o onboarding pela UI — o guard `RequireProfile` redireciona sem perfil). Sem sleeps soltos: apenas expects com auto-retry (timeouts de 10–20 s nas etapas do plano simulado).
- **Justificativa:** preview do build é mais estável que o dev server para CI (sem HMR, sem dependência de watch); o build também valida `tsc -b` antes dos testes.

## D-027 — `MarkdownContent` extraído para shared + `ModalDialog` compartilhado (efeito de montagem única)
- **Decisão:** o renderizador de markdown do chat virou `features/shared/components/markdown-content.tsx` (reuso chat + detalhe de documento); o arquivo antigo do chat ficou como re-export para não quebrar imports. Novo `features/shared/components/modal-dialog.tsx`: dialog centrado com o mesmo contrato de a11y do TaskDrawer (D-022) — `role="dialog"` modal, foco preso, Esc/backdrop fecham. Diferença crítica: o efeito de foco roda UMA vez na montagem e o `onClose` vive em ref — com `[onClose]` nas deps, cada re-render do formulário (cada tecla digitada) reexecutava o efeito e roubava o foco de volta ao gatilho (bug encontrado nos testes da FE-2a). `ApprovalResolveActions` (shared) generaliza o padrão aprovar/reprovar-com-observação de `task-approvals.tsx` para documentos e para a fila consolidada.
- **Justificativa:** reuso real entre três features sem duplicar a11y; o bug de foco só apareceria em formulários dentro de dialogs (todos os novos).

## D-028 — Diff de versões com LCS local (sem dependência)
- **Decisão:** `features/documents/lib/diff.ts` implementa diff linha a linha por LCS (programação dinâmica), retornando linhas `same|added|removed` com numeração das duas versões; renderização em tabela mono com tokens `bg-success/10` (adicionadas) e `bg-error/10` (removidas). Nenhuma lib de diff adicionada.
- **Justificativa:** versões de documentos têm dezenas/centenas de linhas — LCS quadrático é mais que suficiente e evita ~50–200 kB de dependência (diff/diff2html) para um caso simples. Se surgir necessidade de diff por caractere ou side-by-side, a lib entra depois sem mudar a API (`diffLines`).

## D-029 — Vínculo documento↔fase: campo `phaseName` no contrato + comando `classifyDocument`
- **Decisão:** `Document` ganhou `phaseName: string | null` (nome da fase do template — mesmo modelo "nome como chave" dos gates/fases, D-017). Órfão = `phaseName === null`; a seção "Documentos órfãos" do catálogo sugere as fases da versão ativa do workflow. A classificação (rótulos + fase) NÃO é PATCH de documento: virou o comando `POST /documents/<id>/classification` (`classifyDocumentInputSchema`), pois metadados de classificação mudam sem tocar conteúdo/versões — documento permanece fora do `UpdateInputMap`. Fixtures: 7 documentos vinculados a fases, 2 órfãos (`Spec do protótipo v0`, `ADR 002 — SSR`).
- **Justificativa:** a missão exige filtro por fase e detecção/classificação de órfãos; o contrato não modelava o vínculo. Registrado no HANDOFF_API (campos FE-2a).

## D-030 — `Approval` ganha `priority` + `dueAt`; tipo da decisão derivado do vínculo
- **Decisão:** a fila consolidada precisava de criticidade e prazo — adicionados `priority: Priority` (default `medium` na criação) e `dueAt: ISO | null` ao contrato de `Approval` (+ fixtures com prazos distintos). O TIPO do item (gate/documento/tarefa/decisão humana) é derivado em `approvals-derive.ts` pelo vínculo preenchido (`gateId`/`documentId`/`taskId`/nenhum) — sem campo `kind` novo. Ordenação: prazo mais próximo primeiro (sem prazo por último), desempate por criticidade, depois mais antigo. Filtros de prazo: vencidas / próximos 7 dias / sem prazo.
- **Justificativa:** ordenação sensata e filtros exigidos pela missão sem inflar o contrato com um enum redundante.

## D-031 — Versão de workflow nasce publicada via comando `publishWorkflowVersion`
- **Decisão:** criar versão = publicar (versões são imutáveis por contrato) — comando `POST /workflow-templates/<id>/versions` (`publishWorkflowVersionInputSchema`): fases ordenadas, `gatesByPhase`, e os NOVOS campos opcionais de `WorkflowVersion` — `phaseConfigs` (documentos esperados, peso de progresso 0–100, agentes permitidos por definição), `defaultOperationMode`, `transitions` (fase → próximas permitidas). O mock numera como última+1, atualiza `template.currentVersionId` e emite `workflow.versionPublished` no stream `global` e nos `project:<id>` que usam o template. O formulário de nova versão vem pré-preenchido da versão atual (edição de fases por linha de texto, gates por vírgula, checkboxes para documentos/agentes/transições).
- **Justificativa:** o contrato já definia versões como "publicadas e imutáveis" — um estado "rascunho de versão" seria máquina de estados nova sem consumidor nesta fatia. Campos opcionais preservam fixtures/testes existentes.

## D-032 — Resolver aprovação de documento transiciona o documento (mock espelha regra de domínio)
- **Decisão:** no `MockApiClient.resolveApproval`, aprovação com `documentId` e documento em `awaitingApproval` transiciona para `approved` (aprovar) ou `inElaboration` (reprovar — volta para elaboração) e emite `document.stateChanged` além de `approval.resolved`. No detalhe do documento, a decisão mais recente permanece visível após a resolução (badge + observação); "Solicitar aprovação" (doc em `inReview`) transiciona para `awaitingApproval` e cria a aprovação pendente.
- **Justificativa:** a missão exige que aprovar/reprovar documento emita `approval.resolved` + `document.stateChanged`; o mock precisa refletir a regra que o backend terá (documentada no HANDOFF §4).

## D-033 — Locales i18n por feature (módulos JSON mesclados no loader)
- **Decisão:** além do catálogo base (`src/i18n/locales/pt-BR.json` / `en.json`), cada feature tem seus próprios arquivos `src/i18n/locales/<lang>/<feature>.json` com UM namespace de topo exclusivo (ex.: `"orchestrator"`), mesclados pelo loader em `src/i18n/index.ts` via `import.meta.glob` (eager). Colisão de namespace com o catálogo base lança erro na inicialização. Chaves compartilhadas entre features (ex.: `status.chiefTurnState.*`, `status.componentState.*`, `status.notificationSeverity.*`, `common.secrets.maskedNote`) ficam no catálogo base.
- **Justificativa:** a FE-2b adicionou 5 features em paralelo; namespaces por arquivo eliminam conflito de edição no JSON único e mantêm a regra de espelhamento pt-BR/en auditável por arquivo.

## D-034 — Comandos do chefe: pause/resume em `Project.state`, handoff com fencing, drain por eventos existentes
- **Decisão:** quatro comandos novos no `ApiClient` (mock + http, `contracts/commands.ts`): `POST /projects/<id>/chief/pause|resume|handoff|drain`. Pausar/retomar mapeiam em `Project.state` (`paused`/`active` — mesmo mapeamento de D-021: o enum de estados de agente não tem "pausado") + estado do agente chefe (`waiting`/`idle`), com `agent.statusChanged` + `audit.eventAppended`. Handoff (`note` obrigatória) cria NOVA instância de agente (definição/modelo opcionais), reponta `Project.chiefAgentId`, incrementa o fencing token e registra auditoria — o chefe anterior fica `idle`. Drain devolve tarefas em andamento (`development|review|corrections|testsGates`) para `ready` via os mesmos eventos `task.stateChanged` de `moveTask`, cancela attempts running e põe agentes do projeto em `idle`.
- **Justificativa:** a missão pede as 4 ações e o contrato não as cobria; reutilizar estados/eventos existentes evita máquina de estados nova (mesmo critério de D-021/D-025). Documentado no HANDOFF §4 — o backend deve espelhar.

## D-035 — `Agent.modelId` (override) e `Agent.lease` (fencing) no contrato
- **Decisão:** `Agent` ganhou `modelId: Ulid | null` (override da instância; modelo efetivo = `modelId ?? definition.defaultModelId`) e `lease: { fencingToken, expiresAt } | null` (concessão de orquestração do chefe, exibida em "diagnóstico avançado" colapsado). O mock entrega o lease à nova instância no handoff (fencing +1) e limpa o do chefe anterior.
- **Justificativa:** o card do chefe precisa refletir o modelo após a passagem de bastão (gate E2E) e a missão pede lease/fencing visíveis; campos nullable preservam fixtures e o round-trip dos contratos.

## D-036 — `useNow` em shared + máscara de segredos centralizada (`@/lib/secrets`)
- **Decisão:** `useNow` saiu de `features/board/hooks/use-board.ts` para `features/shared/hooks/use-now.ts` (board re-exporta para não quebrar imports — pendência registrada da FE-2a, quitada). Novo `src/lib/secrets.ts`: `maskSecrets` (pares chave=valor: token/api_key/secret/password/authorization/bearer/credential/senha), `maskEndpoint` (userinfo de URL + query sensível) e `SECRET_MASK`, com a nota i18n `common.secrets.maskedNote`. Usado por orchestrator (log da tentativa), tools (endpoint MCP) e governance (detalhe/payload exportado).
- **Justificativa:** a regra "nunca exibir segredos" vale para 3 features; um único utilitário testado evita 3 regexes divergentes.

## D-037 — Métricas objetivas de agentes derivadas do contrato, com definição visível
- **Decisão:** "tarefas concluídas" = `metrics.tasksCompleted`; "aprovadas em revisão" = tarefas do agente (`assigneeAgentId`) em `done`; "retrabalho" = tarefas do agente em `corrections` + attempts `failed`. Derivação em função pura testada (`features/agents/lib/agents-derive.ts`); cada métrica tem ajuda i18n (`agents.metrics.*.help`) com a definição exata exibida na UI.
- **Justificativa:** o contrato não tem campos de aprovação em revisão/retrabalho por agente — derivar de dados reais (com definição visível) respeita a regra de zero dado inventado e deixa a semântica auditável.

## D-038 — Campos fora do contrato exibidos como "indisponível" (tools/skills/plugins/MCP)
- **Decisão:** o catálogo mostra apenas campos reais (nome, descrição, estado, kind/origem, version, transport, toolCount, endpoint mascarado). Checksum, permissões, risk tier e projetos autorizados — pedidos pela missão mas inexistentes nos schemas — aparecem como "não disponível no contrato atual" (chave `tools.unavailable`) e estão registrados como pendência no HANDOFF §4. Habilitar/desabilitar usa o PATCH genérico de `state` (já no `UpdateInputMap`) com dialog de confirmação; `tool.statusChanged` cobre tools via realtime, demais por invalidação pós-mutation.
- **Justificativa:** zero dado fabricado é regra inegociável do projeto; a chave i18n já está pronta para quando o contrato incluir os campos.

## D-039 — Governança: correlação por `targetType`/`targetId` e exportação client-side mascarada
- **Decisão:** filtros por usuário/agente/tarefa/tentativa/modelo/ferramenta casam por `actorId` ou `targetId` conforme o `targetType`; projeto resolve pelo `projectId` da entidade alvo (attempt → task). Evento sem alvo correlacionável (ex.: `license.validated` com `targetId` nulo) só aparece com o filtro em "todos" — regra documentada em `features/governance/lib/audit-derive.ts` e coberta por testes. Expansão do evento mostra detail mascarado + correlação (attempt → custo/tokens/duração; approval → resolução; task → título/estado). Exportação JSON/CSV é 100% client-side (Blob + `<a download>`) dos eventos filtrados, com `detail` passado por `maskSecrets` também no arquivo. Realtime `audit.eventAppended` (stream global) invalida a timeline.
- **Justificativa:** `AuditEvent` não tem `projectId` nem payload estruturado — a correlação por alvo é a interpretação mais simples e honesta do contrato; mascarar também na exportação mantém a regra de segredos fora da UI (e dos artefatos gerados por ela).

## D-040 — Fixture corrigida: documento com aprovação pendente passa a estar em `awaitingApproval`
- **Decisão:** a "Spec da API v1" (única aprovação pendente de documento das fixtures) estava em `inReview`; corrigida para `awaitingApproval`, alinhando a fixture à premissa da D-032 — o mock só transiciona o documento ao resolver a aprovação se ele estiver `awaitingApproval`.
- **Justificativa:** sem a correção, o gate E2E de aprovação de documento não teria como provar `approval.resolved` + `document.stateChanged` pela UI.

## D-041 — Gate E2E FE-2: aprovação/reprovação de documento + passagem de bastão
- **Decisão:** `e2e/fe2-flows.spec.ts` (3 testes × mobile-360/desktop-1440): aprovar documento pendente → badge `Aprovado`; reprovar exige observação (erro sem nota; com nota → documento volta a `Em elaboração`); handoff no orquestrador (wizard: modelo + motivo + confirmação) → card do chefe reflete o novo modelo sem reload. Mesmas técnicas do gate FE-1 (D-026): onboarding pela UI no setup, `navTo` por viewport, só expects com auto-retry.
- **Justificativa:** cobre os dois fluxos-crítico da missão FE-2b contra o preview do build, sem sleeps; a observação em aprovação não existe na UI atual (`ApprovalResolveActions` envia a decisão sem nota — observação só é obrigatória na reprovação), então o spec valida o comportamento real.

## D-042 — `Project.prototyping` com waiver obrigatório em `notApplicable`
- **Decisão:** `Project` ganhou `prototyping: { mode: PrototypingMode, waiver: { reason, grantedAt } | null }` (enum `externalPrototype | guidelinesOnly | autonomousGeneration | notApplicable`). Quando `mode = notApplicable`, o schema exige waiver preenchido (motivo + data) — espelha a regra de waiver de documentos. Fixtures: poseidon `autonomousGeneration`, pagamentos `notApplicable` com waiver. `prototyping` entrou no Pick de `projects` no `UpdateInputMap` (PATCH pela tela de protótipos).
- **Justificativa:** a missão FE-3 pede cenário de prototipação por projeto com dispensa justificada; o contrato não modelava nada disso. Registrado no HANDOFF_API (campos FE-3).

## D-043 — PATCH de `conversations` (`title`/`state`); filtro por canal indisponível
- **Decisão:** `UpdateInputMap.conversations = Partial<Pick<Conversation, "title" | "state">>` — renomear e arquivar/desarquivar são PATCH genérico, sem comandos novos. O chat passou a ler `?conversation=<id>` (deep link da tela de conversas) e a lista inclui conversas arquivadas. O filtro por CANAL pedido pela missão não foi implementado: `Conversation` não tem campo de canal — segue o precedente da D-038 (não fabricar dado), registrado como pendência no HANDOFF §4.
- **Justificativa:** PATCH genérico cobre as duas ações sem inflar a API de comandos; zero dado fabricado é regra inegociável.

## D-044 — Comandos de run-targets; logs no stream do projeto com `runId` nulo
- **Decisão:** `startRunTarget`/`stopRunTarget`/`restartRunTarget` (`POST /run-targets/<id>/start|stop|restart`) mudam `RunTarget.state` e emitem `run.logAppended` no stream `project:<id>` com `runId`/`attemptId` NULOS (não há WorkflowRun envolvido — subir serviço local não é run de workflow). O nível da linha (info/erro) é derivado do texto na UI, pois o evento não tem campo de nível (pendência análoga à de `AttemptEvent`, FE-2b). `cleanupRunEnvironment` (`POST /projects/<id>/run-environment/cleanup`) retorna contagem e audita (`run.environmentCleaned`); exige confirmação na UI. Credenciais demo não existem no contrato — a UI mostra valores estáticos mascarados via i18n.
- **Justificativa:** reusar `run.logAppended` evita evento novo; o payload com IDs nulos deixa explícito que o log é do ambiente de run, não de uma tentativa.

## D-045 — `analyzeSolicitation` determinístico cria a solicitação; demanda via CRUD existente
- **Decisão:** comando `POST /solicitations/analyze` (`{ projectId, text, attachmentNames? }`) → `SolicitationAnalysis` (5 painéis de itens `{ id, text }`: requisitos, dúvidas, contradições, riscos, sugestões). O mock é determinístico (sentenças do texto viram requisitos; pergunta fixa de prazo; gatilho `[contradição]` gera contradição) e CRIA a solicitação `kind: request` como efeito colateral. A curadoria dos itens é local à página; "Criar demanda" usa o CRUD de `demands` (título derivado do primeiro requisito) + evento `demand.created` existente. Constantes exportadas (`PO_ANALYSIS_QUESTION_DEADLINE`) para testes/E2E.
- **Justificativa:** a missão pede análise → curadoria → demanda sem inventar endpoints de LLM; o determinismo do mock torna o gate E2E estável sem mocks de rede.

## D-046 — `activateLicense` com regex; backup/diagnóstico como comandos auditados
- **Decisão:** `POST /licenses/activation` valida a chave no formato `XXXX-XXXX-XXXX-XXXX` (regex no schema — 400 em formato inválido), ativa a licença e audita (`license.activated`). `POST /backups` / `POST /backups/<id>/restore` retornam/registram `BackupHandle` com auditoria. `GET /diagnostics` monta o relatório a partir de `product` (`src/config/product.ts`, estendido com `version`/`codename`). Settings usa PATCH genérico (`Settings` já estava no `UpdateInputMap`) para idioma/tema/workdir; revogar `unsafeMode` tem dialog de confirmação.
- **Justificativa:** a missão FE-3 pede essas ações e o contrato não as cobria; validação no schema Zod garante mock e http-client com a mesma regra.

## D-047 — Metadados de referência visual via tags prefixadas; versões de protótipo indisponíveis
- **Decisão:** briefing de origem e momento do fluxo (pedidos pela missão) não existem em `VisualReference` — a UI usa a convenção de **tags prefixadas** (`briefing:<nome>`, `fluxo:<momento>`) para agrupar/filtrar a galeria, sem campo novo no contrato. Upload de ZIP exibe aviso de extração local; histórico de versões de protótipo não existe (sem `prototype-versions`) — a UI mostra só o estado atual. Ambos registrados como pendência no HANDOFF §4.
- **Justificativa:** tags já existem no schema — a convenção prefixada entrega a UX pedida sem fabricar campos; formalização fica para o backend.

## D-048 — Gate E2E FE-3: rodar projeto + PO Assistant
- **Decisão:** `e2e/fe3-flows.spec.ts` (2 testes × mobile-360/desktop-1440): "rodar projeto" — 'Iniciar tudo' → painel `role="log"` mostra `Iniciando "Frontend Vite (dev)"` e health check; 'Parar tudo' → log de parada. "PO Assistant" — preencher pedido → 'Analisar pedido' → 5 painéis + pergunta fixa de prazo → 'Criar demanda estruturada' → título da demanda visível → link 'Ver no quadro' → heading 'Quadro'. Mesmas técnicas dos gates anteriores (onboarding pela UI, `navTo` por viewport, expects com auto-retry; timeout 10 s cobre o delay de 400 ms dos logs do mock).
- **Justificativa:** cobre os dois fluxos-críticos da missão FE-3 contra o preview do build; o assert do título usa `.first()` porque o texto também aparece no textarea e na lista de requisitos (strict mode do Playwright).

## D-049 — Gate de acessibilidade axe-core em todas as rotas (2 viewports × 2 temas)
- **Decisão:** `e2e/a11y.spec.ts` visita as 21 rotas + onboarding nos projetos mobile-360 e desktop-1440, rodando axe (tags wcag2a/2aa/21a/21aa) e falhando em violações `critical`/`serious`. Cada rota é escaneada em dark e em light (preferência persistida via `localStorage 'poseidon-theme'` + reload). Script `test:a11y` dedicado; o spec também roda dentro de `test:e2e`. Correção sistêmica encontrada pelo gate: containers de skeleton usavam `aria-label` em `<div>` sem role (violação serious `aria-prohibited-attr`) — padronizados com `role="status"` (25 arquivos).
- **Justificativa:** acessibilidade WCAG 2.1 AA é convenção inegociável do projeto e precisava de gate automatizado; `role="status"` é o padrão ARIA correto para indicadores de carregamento e não altera o visual.

## D-050 — `manualChunks` por família de vendor
- **Decisão:** `vite.config.ts` separa `vendor-react` (react/react-dom/router), `vendor-query`, `vendor-forms` (react-hook-form/zod), `vendor-i18n`, `vendor-markdown` e `vendor-signalr`. O chunk principal saiu de >500 kB para ~220 kB (min); features continuam lazy por rota via `import.meta.glob`.
- **Justificativa:** vendors mudam raramente — chunks separados cacheiam melhor e o warning de >500 kB do build desaparece; famílias estáveis, sem micro-otimização por módulo.

## D-051 — Higiene i18n automatizada (sweep estático + paridade)
- **Decisão:** `src/i18n/__tests__/i18n-hygiene.test.ts` (Vitest, roda no `npm run check`): (1) varredura AST (TypeScript compiler API) de `src/**/*.tsx` falhando em literal visível com letras em JSXText, `{'...'}` ou atributos `aria-label`/`placeholder`/`title`/`alt` — símbolos/números passam, exceções só via ALLOWLIST explícita (vazia); exclui testes, stories e `src/test/`. (2) paridade das árvores de chaves pt-BR ↔ en dos catálogos mesclados (base + módulos por feature). Sweep inicial achou 2 ocorrências, movidas aos catálogos: `URL` (run-project) e o placeholder `~/poseidon` (onboarding).
- **Justificativa:** "zero string hardcoded" é convenção inegociável — sem teste, regride silenciosamente; AST evita falsos positivos de regex em código.

## D-052 — Estado "permissão negada": N/A no contrato atual; auditoria de estados em SCREENS.md
- **Decisão:** o 5º estado obrigatório (permissão negada) é NÃO SE APLICA em todas as telas: a camada `src/api/` não modela 401/403 (nem o mock nem o http-client distinguem forbidden). Quando o backend introduzir autorização, adicionar o conceito ao `ApiError`/cliente e o estado às telas. A auditoria dos 5 estados nas 21 telas ficou registrada em `docs/frontend/SCREENS.md`; lacunas encontradas e corrigidas: estados vazios de providers (página/budgets/roteamento) e retry do erro de `workflow-templates` no detalhe de organizations. Retry com botão é padrão para queries; erros de mutation usam `role="alert"` reenviável pela própria ação (padrão consistente do app).
- **Justificativa:** não fabricar tratamento de um erro que o contrato não emite (mesmo critério de D-038/D-043); registrar como pendência mantém o requisito rastreável.
