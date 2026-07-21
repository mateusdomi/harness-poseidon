# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase:** refinamento funcional FR-1 a FR-5 concluído no frontend em 2026-07-19; a evidência detalhada está em `REFINEMENT_AUDIT.md`.
- **Branch:** somente `develop`. Nunca fazer merge em `main` sem autorização explícita.
- **Escopo de autoria:** somente `frontend/**` e `docs/frontend/**`. Há trabalho de backend em paralelo; antes de publicar, buscar `origin/develop`, incorporar apenas o avanço remoto e adicionar ao commit somente esses dois diretórios.
- **Design:** o design system, tokens, temas, logo, cores, tipografia e padrões responsivos existentes foram preservados. O trabalho foi incremental.
- **Integração:** contas/providers/modelos e o lifecycle V3 de `agent-definitions` estão reconciliados com `docs/contracts/openapi.json`; o catálogo de eventos e o snapshot realtime têm validação de contrato. Alguns metadados complementares do refinamento (time, stacks, effort/account/fallback padrão, actor/critic, risco e histórico legível) ainda são mock-only e estão registrados em `HANDOFF_API.md`.

## Reconstrução da jornada inicial e UX do golden path — 2026-07-21

- **Objetivo:** a RC3 estava tecnicamente verde, mas a homologação humana
  mostrou que o primeiro uso ainda exigia conhecimento interno do produto. Esta
  fase transforma a jornada vazia em um fluxo guiado até a execução real do
  Chief, sem redesenhar o Poseidon.
- **Escopo respeitado:** somente `frontend/**` e `docs/frontend/**`, na
  `develop`. `governance/manifest.yaml` não foi tocado, não houve merge em
  `main` e **nenhum GNG foi declarado**.

### Prontidão do golden path (fonte da verdade)

- `features/onboarding/lib/golden-path.ts` deriva 8 etapas (perfil →
  organização → projeto → provedor → modelo → workflow → Chief pronto →
  primeira execução) **a partir de recursos reais**; `use-golden-path.ts`
  reutiliza as query keys das features (cache compartilhado, sem refetch).
- **Não existe readiness canônico no OpenAPI.** As etapas `chief` e `firstRun`
  são heurísticas conservadoras (fail-closed) registradas em `HANDOFF_API.md`
  §9.1. O frontend não duplica fonte da verdade: só apresenta o derivado.
- Checklist persistente no Cockpit com status, explicação, CTA única,
  bloqueador e deep link por etapa; some quando o caminho está completo.

### Honestidade operacional

- Orquestrador expõe prontidão real (`notConfigured`, `awaitingProvider`,
  `awaitingWorkflow`, `ready`, `running`, `degraded`) com a CTA correspondente.
- O padrão de modelo vindo da **definição** deixou de ser apresentado como
  vínculo "em uso": agora recebe o rótulo **"Binding pendente"**.
- **"Modo simulado"** é explícito onde há prontidão/cotas (Cockpit e
  Orquestrador) sempre que a origem dos dados é fixture (`VITE_API_MODE`
  diferente de `http`).
- Atividade recente humanizada em PT/EN, com ator, horário, objeto, resultado,
  ícone e link. O **código cru só aparece no disclosure "Ver detalhes"**, e
  ação desconhecida degrada para o `detail` do servidor — nunca texto inventado.

### Jornada e formulários

- **Pré-condição organização → projeto:** sem organização, a tela de Projetos
  explica o vínculo em vez de abrir um select vazio; ao criar a organização, o
  usuário volta ao fluxo com ela pré-selecionada (`?new=1&org=<id>`).
- **CTA única por ação:** em coleção vazia só o empty state age; o botão do
  topo aparece quando já existe item. Vale para organizações, projetos e chat.
- **Identificador da URL** (antes "Slug") é gerado do nome, vive em seção
  avançada, com validação em tempo real e prévia; a sigla do projeto também é
  derivada. O **plano saiu do formulário** — é read-only derivado da licença.
- **Voltar/breadcrumb compartilhados** (`BackLink`, `Breadcrumb`,
  `PageHeader`): seta, rótulo específico, histórico com fallback para a
  rota-pai, acessível por teclado e independente do menu lateral.
- **Definição de agente** virou formulário guiado em 6 passos com descrição,
  exemplo e impacto por campo; chave técnica derivada e bloqueada na edição;
  esforço dirigido por `Model.effortMappings` reais (incompatível é
  desabilitado, nunca ignorado em silêncio); catálogos vazios levam à tela de
  gestão; template/importação JSON com prévia, erro por campo e rejeição de
  segredo.
- **Marca** redesenhada: prévia da logo com remoção, color picker + HEX para as
  duas cores canônicas, tipografia como presets explicados e prévia da marca.
  **Não há contrato de upload/crop de logo** (§9.4 do HANDOFF).
- **Chat inicial:** composer pronto sem conversa (criada de forma idempotente
  ao enviar) e CTA explícita; faltando provedor/modelo/workflow, **apenas a
  execução** é bloqueada, sempre com o motivo e a CTA. O acknowledgement do
  turno deixou de ser estilizado como resposta do Chief.
- **Workflow inicial** explica o conceito, recomenda um template publicado
  real, resume as fases e vincula em um clique (§9.5 do HANDOFF).

### Evidência desta fase

- `npm run check`: lint e typecheck limpos; **57 arquivos e 484 testes** verdes.
- `npm run build` e `npm run build-storybook`: verdes (só os avisos conhecidos
  de PURE do SignalR e de `eval`/chunk do Storybook).
- `npm run test:e2e`: **72/72** em mobile-360 e desktop-1440, incluindo o novo
  gate `golden-path-ux.spec.ts` e o FE-1 percorrendo o caminho completo
  (bloqueio honesto → workflow recomendado → conversa automática).
- `npm run test:a11y`: **42/42** (21 rotas em mobile/dark e desktop/light).
- `npm audit --omit=dev`: **0 vulnerabilidades de produção**.
- Validação visual no navegador (mock, 1440×1250) do checklist, do bloqueio do
  chat, do workflow recomendado e da prontidão do orquestrador.

### Pendências reais (não declaradas como concluídas)

- `npm run test:e2e:real` e `npm run test:e2e:package` **não foram executados**
  nesta sessão: exigem, respectivamente, o Host .NET real e o pacote
  self-contained instalado (`POSEIDON_PACKAGE_APP_DIR`), indisponíveis aqui. O
  recorte de "zero organização" vive em `package-clean.spec.ts` e **precisa ser
  rodado no ambiente com pacote** antes de qualquer declaração de gate.
- As lacunas de contrato do §9 do `HANDOFF_API.md` seguem abertas no backend.

## Correção da RC `d11df77` — skeleton infinito na primeira abertura

- **Causa comprovada no pacote:** com data dir vazio, `GET /api/v1/projects` retornava `200` e lista vazia. As queries dependentes de projeto (`tasks`, `approvals`, `agents`, workflows e equivalentes nas demais features) não eram iniciadas por `enabled: false`, mas o TanStack Query v5 mantém `isPending: true` nesse estado. A UI agregava `isPending` como se houvesse fetch ativo e renderizava skeleton para sempre.
- **Reprodução controlada:** pacote self-contained em `127.0.0.1:5097`, contexto Chromium limpo e sem extensões. Após selecionar o perfil, todas as seis requests observadas terminaram em 2–15 ms (`profiles`, `projects`, `notifications/unread`, `profiles/current`, `budgets`, `audit-events`), nenhuma ficou pending e quatro skeletons permaneceram. A mensagem “Receiving end does not exist” não apareceu no perfil limpo e não é a causa.
- **Correção:** agregadores de leitura usam `isLoading` (pending **e** fetching); query desabilitada produz estado vazio/onboarding, nunca loading. O guard valida `profiles/current`, recupera 401/404 ou divergência para onboarding e apresenta 403/erro com retry. GETs lentos emitem telemetria local redigida após 4 s e terminam explicitamente em 504 após 10 s; escritas não são canceladas pelo frontend. O fallback de rota troca skeleton por erro com retry após 10 s.
- **Pacote limpo:** `npm run test:e2e:package` inicia o self-contained sem `--demo`, cria e remove um data dir temporário vazio e usa Chromium novo com service workers bloqueados. O spec cobre primeira abertura, perfil, organização, projeto, 21 rotas, ausência de skeleton após 12 s, same-origin, build sem 5090/5173, cookie inexistente, API indisponível, 401/403/404/409/500, JSON inválido, reconnect, zero erro da aplicação e zero asset 404.
- **Limite de contrato:** o cookie `harness.profile` é HttpOnly e o OpenAPI não publica comando para selecionar/revogar perfil. Um cookie válido no formato, mas apontando para perfil inexistente, recupera para onboarding; reutilizar outro perfil já existente depende de contrato backend adicional, registrado em `HANDOFF_API.md`.

### Evidência técnica desta correção

- `npm run check`: lint/typecheck verdes; 53 arquivos e 444 testes unitários/componentes verdes.
- `npm run build`: verde; apenas os dois avisos conhecidos do Rollup sobre anotação PURE do SignalR.
- `npm run build-storybook`: verde; avisos conhecidos do toolchain sobre `eval` e chunks grandes.
- `npm run test:e2e`: 56/56 verdes no mock, após excluir explicitamente o spec exclusivo do pacote.
- `npm run test:a11y`: 42/42 verdes (21 telas em mobile/dark e desktop/light).
- `npm run test:e2e:real`: 3/3 verdes contra Host self-contained isolado em `127.0.0.1:5100`.
- `npm run test:e2e:package`: 1/1 verde em 38,1 s contra o pacote self-contained final, sem demo e com data dir efêmero vazio.
- `npm audit --omit=dev`: 0 vulnerabilidades de produção. Portas temporárias 5098/5099/5100 encerradas e data dirs de teste removidos.

## Integração de governança P2 — 2026-07-20

- **Base sincronizada:** `origin/develop` em `94061f4`, incluindo os contratos P2 dos commits `f2b35a6` e `94061f4`.
- **Contratos reconciliados:** OpenAPI SHA-256 `271ca1dfa7be947783e71793333989e1a4d2bbc2287d0de9da8c35503482763d`; eventos 1.1 SHA-256 `093d8c9c9d85db4fa17551085060478a6e23149a01b4e1684760936c8ed6a554`.
- **UI real:** a aba Aprendizado da feature Governança integra lista cursor-paginada, filtros, detalhe, evidência, comparação, revisão, solicitação/registro da avaliação independente, shadow validation, decisão, promoção manual, monitoramento, rollback, depreciação, histórico e métricas.
- **Promoção:** exclusivamente por ação humana explícita, com confirmação adicional na UI; não existe caminho automático no frontend.
- **Realtime:** o catálogo 1.1 ainda não publica evento específico de learning candidate. A UI assina somente o evento canônico `audit.eventAppended` no stream `global` e invalida as queries P2; nenhum nome de evento foi inventado.
- **Permissões e masking:** 401/403 seguem o tratamento transversal; ações administrativas exibem seu requisito e preservam o erro contextual. Texto potencialmente sensível é mascarado antes de renderizar. O servidor continua sendo a autoridade de autorização e redaction.
- **Limite contratual conhecido:** período não existe como parâmetro da listagem P2; portanto datas filtram apenas as páginas já carregadas e a interface informa isso. Projeto, tipo, estado, cursor e limite são filtros server-side.
- **Homologação real:** `npm run test:e2e:real` percorreu o lifecycle completo contra o Host real até depreciação em desktop, além de smoke responsivo em tablet/mobile, axe, console e assets: 3/3 verdes, zero console error e zero asset 404.

## Baseline da homologação final — 2026-07-20

- **SHA sincronizado:** `2e9928ffac536dbde843a9b683e68e865e6c4f0b` (`origin/develop`, incorporado com `git pull --no-rebase`).
- **Re-sincronização pré-publicação:** `origin/develop` avançou para `1865381` (Gate P0 do backend) e foi incorporado por fast-forward com `git pull --no-rebase`; OpenAPI/eventos permaneceram byte a byte iguais, portanto não abriram o Gate P1.
- **Dependências:** `npm ci` concluído. `npm audit --omit=dev` reporta **0 vulnerabilidades de produção**. O audit completo reporta 8 no toolchain de desenvolvimento (6 moderadas, 1 alta e 1 crítica: árvore Storybook/Vite/Vitest); nenhuma correção forçada/major foi aplicada durante a homologação.
- **Gate estático/unitário:** `npm run check` verde — lint e typecheck sem erros; 52 arquivos e 437 testes Vitest aprovados.
- **Build:** `npm run build` verde; apenas os avisos conhecidos do pacote SignalR/Rollup sobre anotações `/*#__PURE__*/`.
- **Storybook:** `npm run build-storybook` verde; apenas avisos de dependências do Storybook (uso de `eval` e chunks do preview acima de 500 kB).
- **E2E + a11y em mock determinístico:** `npm run test:e2e` verde — 56/56 cenários Playwright nos projetos mobile-360 e desktop-1440; o spec de acessibilidade percorreu onboarding + 21 rotas em dark/light sem violações critical/serious.
- **Backend real:** Host .NET isolado em `127.0.0.1:5090`, SQLite temporário com 46 migrations, frontend same-origin em `127.0.0.1:5173`. O gate `npm run test:e2e:real` percorre onboarding + 21 rotas em desktop 13", tablet e mobile, dark/light, axe AA, console/assets, além dos fluxos reais descritos abaixo.
- **Governança P1/P2:** contratos reais publicados e integrados sob a flag operacional `VITE_GOVERNANCE_CONTRACT_UI=on`. O modo `off` continua como rollback e mantém somente a auditoria histórica; P1 cobre receipts/métricas, avaliação independente, stale findings, hashline/benchmark, executores e diagnóstico.

## Resultado da Parte A contra o Host real

- Onboarding, sessão limpa, criação/seleção de perfil, organização e projeto executados pela UI real.
- Deep links das 21 rotas exercitados em `1280×800` dark, `820×1180` light e `360×800` dark; zero erro não tratado de console e zero resposta quebrada de font/image/script/stylesheet.
- Axe WCAG 2 A/AA e 2.1 A/AA sem violações `critical`/`serious` nas rotas e temas exercitados. Contrastes brand/primary e semântica da lista de progresso foram corrigidos.
- Busca global validada pelo atalho do SO, foco inicial no combobox, navegação por teclado e foco visível. O modal agora aceita alvo de foco inicial sem disputa entre efeitos.
- SignalR comprovado com turno real: `message.appended`, `chat.turnStarted`, `chat.turnChunk` e `chat.turnCompleted`; snapshot REST ordenado por sequence; WebSocket interrompido no proxy exclusivo do gate; banner transitório, reconexão automática, nova assinatura/snapshot e segundo turno concluído.
- Fluxos adicionais reais: upload de documento markdown e deep link do detalhe; sincronização de catálogo de provider; informações do modo local/Launcher e diretório de trabalho; CSV da auditoria baixado como `poseidon-auditoria.csv`.
- O tratamento transversal de autorização agora converte 401 em expiração da sessão/onboarding e 403 de query em estado de permissão negada com retry, disponível para todas as rotas no shell.
- Evidências visuais automatizadas ficam em `docs/frontend/evidence/2026-07-20-http-real/`; o roteiro de homologação humana está no final de `SCREENS.md`. A inspeção humana final continua sendo decisão do usuário, não uma declaração deste gate.

### Correções encontradas durante a homologação

- Assinaturas criadas antes do `connect()` não eram enviadas ao hub. O cliente agora assina todos os streams após conectar/reconectar, elimina rejeições não tratadas e usa WebSocket direto no Host.
- Em lacuna de sequence, o tracker avançava cedo demais. Agora preserva o último sequence contínuo, aplica snapshot ordenado, descarta duplicatas e re-sincroniza também após reconexão.
- Tokens de brand tinham contraste insuficiente em light/dark em badges, links, tabs e ações. Os componentes passam a usar os pares semânticos `primary/primary-foreground` e `brand-strong`.
- A busca global perdia o foco para o modal; o contrato de `initialFocusRef` corrige o comportamento.

### Gates finais executados

- `npm run check`: 52 arquivos / 437 testes, lint e typecheck verdes.
- `npm run build`: verde; somente os dois avisos conhecidos de anotação PURE do SignalR.
- `npm run build-storybook`: verde; avisos conhecidos de `eval`/chunk no toolchain do Storybook.
- `npm run test:e2e`: 56/56 verdes no mock, mobile 360 e desktop 1440.
- `npm run test:a11y`: 42/42 verdes, 21 rotas em mobile/dark e desktop/light.
- `npm run test:e2e:real`: 3/3 verdes, Host real, desktop 13"/tablet/mobile, dark/light, axe, console/assets, SignalR/snapshot/reconnect e fluxos críticos.
- `npm audit --omit=dev`: 0 vulnerabilidades.

## Refinamentos entregues

- FR-1: paginação 15/30/50, command palette, ordem do menu, assets locais e flags do React Router.
- FR-2: período 24h/3d/7d no Cockpit e painel de workflow no Chat, responsivo e com deep-links.
- FR-3: revisão manual/versionada de documentos; busca, filtros, arquivamento, CSV e explicação de fluxo no Quadro.
- FR-4: ciclo completo de templates/versões de workflow e impacto/versionamento de projetos.
- FR-5: CRUD de definições no Orquestrador, organograma operacional em Agentes, explicação do modo local em Executar projeto e gestão de múltiplas contas/metadados/effort mapping em Provedores.

## Regras permanentes

- Não recriar o frontend nem alterar o backend durante refinamentos de UI.
- Não inventar silenciosamente contrato ausente: Zod + mock + teste + `HANDOFF_API.md`.
- Segredos são recebidos apenas como referência segura (`keychain://`, `dpapi://` ou `secret://`) e nunca retornados/exibidos.
- Estados de coleção: vazio orientado, skeleton, erro com retry e reconexão global; 401/403 são tratados a partir das respostas autoritativas do Host.
- Zero string visível fora do i18n, zero cor fora dos tokens e zero status sem enum/mapeamento.

## Correção transversal do Checkbox — 2026-07-20

- **Defeito (P2 usabilidade/a11y, transversal):** na homologação, marcar um checkbox mostrava só um contorno accent, sem checkmark perceptível; estado marcado/desmarcado ambíguo e dependente apenas de cor. Reportado em Notificações (toggle global + categorias), mas na raiz do componente compartilhado.
- **Causa raiz comprovada:** `src/design-system/components/checkbox.tsx` renderizava o check via `background-image` com data-URI de SVG cujo `stroke` era `var(--color-accent-foreground)`. Custom properties CSS **não resolvem dentro de data-URI de SVG**, então o traço ficava sem cor e invisível — sobrava só o preenchimento/borda accent.
- **Correção (no componente raiz, não por telas):** o check e o traço de `indeterminate` passaram a ser SVGs reais (lucide `Check`/`Minus`) sobrepostos ao input `appearance-none`, revelados por `peer-checked`/`peer-data-[indeterminate]` e coloridos por `currentColor` (`text-accent-foreground`). O estado marcado combina preenchimento + ícone (não depende só de cor). Adicionados `hover`, `focus-visible` (anel `ring-brand` + offset), `active`, `disabled`, `aria-invalid`, transição curta `motion-safe`, e `indeterminate` via propriedade nativa do DOM (leitor de tela anuncia "mixed"). Todos os 12 consumidores herdaram o fix sem alteração de tela.
- **Reforço transversal (Seção 7, só o comprovado):** o `<input type="checkbox">` cru de `learning-candidates-panel.tsx` (promoção manual P2) migrou para o primitive. Três controles de seleção que sinalizavam estado só por cor ganharam cue não-cromático (peso da fonte + leve elevação/anel, sem redesenho): seletor de período do Cockpit, chips de filtro do Chat e abas do catálogo de Ferramentas.
- **Testes:** `checkbox.test.tsx` (10 casos: unchecked/checked/indeterminate/disabled/teclado/label/focus-visible/invalid/ref) valida a **presença do indicador**, não só `checked`; `checkbox.stories.tsx` documenta a matriz de estados; `e2e/checkbox-visual.spec.ts` valida a **opacidade computada** do check em dark/light nos 2 viewports + axe. Evidência visual em `evidence/2026-07-20-checkbox-fix/`.
- **Gates:** `npm run check` verde (lint/typecheck; 54 arquivos, 454 testes). `build` e `build-storybook` verdes (só avisos conhecidos). `test:e2e` 60/60 e `test:a11y` 42/42 verdes (zero console error, zero asset 404). `npm audit --omit=dev`: 0 vulnerabilidades de produção. `test:e2e:real`/`test:e2e:package` seguem pendentes do Host real backend (5090 fora do ar nesta sessão) e da publicação da correção do 500 pelo agente backend — mudança é frontend puro (CSS/DOM), comportamento idêntico contra o Host real.
- **Governança 500 (stale/document findings):** não tratada no frontend por decisão de escopo; a UI já apresenta erro com retry e Problem Details sanitizado sem quebrar a página (tratamento transversal existente). Causa raiz é do agente backend.

## Como validar

```bash
cd frontend
npm ci
npm run check
npm run build
npm run build-storybook
npm run test:e2e
npm run test:a11y
```

O modo de integração comum usa `VITE_API_MODE=http` e `VITE_API_BASE_URL`; o gate local reexecutável usa `POSEIDON_BACKEND_URL=http://127.0.0.1:5090 npm run test:e2e:real`. O modo padrão continua sendo o mock determinístico.
