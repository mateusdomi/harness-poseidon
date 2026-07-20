# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase:** refinamento funcional FR-1 a FR-5 concluído no frontend em 2026-07-19; a evidência detalhada está em `REFINEMENT_AUDIT.md`.
- **Branch:** somente `develop`. Nunca fazer merge em `main` sem autorização explícita.
- **Escopo de autoria:** somente `frontend/**` e `docs/frontend/**`. Há trabalho de backend em paralelo; antes de publicar, buscar `origin/develop`, incorporar apenas o avanço remoto e adicionar ao commit somente esses dois diretórios.
- **Design:** o design system, tokens, temas, logo, cores, tipografia e padrões responsivos existentes foram preservados. O trabalho foi incremental.
- **Integração:** contas/providers/modelos e o lifecycle V3 de `agent-definitions` estão reconciliados com `docs/contracts/openapi.json`; o catálogo de eventos e o snapshot realtime têm validação de contrato. Alguns metadados complementares do refinamento (time, stacks, effort/account/fallback padrão, actor/critic, risco e histórico legível) ainda são mock-only e estão registrados em `HANDOFF_API.md`.

## Baseline da homologação final — 2026-07-20

- **SHA sincronizado:** `2e9928ffac536dbde843a9b683e68e865e6c4f0b` (`origin/develop`, incorporado com `git pull --no-rebase`).
- **Re-sincronização pré-publicação:** `origin/develop` avançou para `1865381` (Gate P0 do backend) e foi incorporado por fast-forward com `git pull --no-rebase`; OpenAPI/eventos permaneceram byte a byte iguais, portanto não abriram o Gate P1.
- **Dependências:** `npm ci` concluído. `npm audit --omit=dev` reporta **0 vulnerabilidades de produção**. O audit completo reporta 8 no toolchain de desenvolvimento (6 moderadas, 1 alta e 1 crítica: árvore Storybook/Vite/Vitest); nenhuma correção forçada/major foi aplicada durante a homologação.
- **Gate estático/unitário:** `npm run check` verde — lint e typecheck sem erros; 47 arquivos e 410 testes Vitest aprovados.
- **Build:** `npm run build` verde; apenas os avisos conhecidos do pacote SignalR/Rollup sobre anotações `/*#__PURE__*/`.
- **Storybook:** `npm run build-storybook` verde; apenas avisos de dependências do Storybook (uso de `eval` e chunks do preview acima de 500 kB).
- **E2E + a11y em mock determinístico:** `npm run test:e2e` verde — 56/56 cenários Playwright nos projetos mobile-360 e desktop-1440; o spec de acessibilidade percorreu onboarding + 21 rotas em dark/light sem violações critical/serious.
- **Backend real:** Host .NET isolado em `127.0.0.1:5090`, SQLite temporário com 44 migrations, frontend same-origin em `127.0.0.1:5173`. O gate `npm run test:e2e:real` percorre onboarding + 21 rotas em desktop 13", tablet e mobile, dark/light, axe AA, console/assets, além dos fluxos reais descritos abaixo.
- **Gate P1 de governança:** indisponível no contrato sincronizado. O OpenAPI ainda não publica manifest documental, saúde/findings de governança, context bundles/receipts ou evaluations, e `events.json` não contém eventos desses domínios. `VITE_GOVERNANCE_CONTRACT_UI=off` é fail-closed e a Parte B não foi apresentada como funcional.

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

- `npm run check`: 49 arquivos / 413 testes, lint e typecheck verdes.
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
- Estados de coleção: vazio orientado, skeleton, erro com retry e reconexão global; 401/403 ainda dependem do contrato de autorização.
- Zero string visível fora do i18n, zero cor fora dos tokens e zero status sem enum/mapeamento.

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
