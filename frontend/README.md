# Poseidon — Frontend

Frontend do **Poseidon** (Harness Poseidon): fábrica autônoma de software onde um agente chefe coordena agentes de IA que desenvolvem projetos reais.

## Stack

- React 18 + TypeScript (strict) + Vite 5
- Tailwind CSS 3.4 + tokens semânticos em CSS variables (`src/design-system/tokens.css`) — dark/light
- Componentes base estilo shadcn/ui copiados em `src/design-system/`
- @tanstack/react-query (server state) · zustand (tema/layout/sessão) · react-router-dom (lazy por rota)
- react-hook-form + zod · react-i18next (pt-BR padrão, en) · @microsoft/signalr · msw
- Vitest + Testing Library · Playwright (+ @axe-core/playwright) · Storybook · ESLint (flat) + Prettier

## Como rodar (modo mock — default)

```bash
npm install
npm run dev            # http://localhost:5173
```

Não precisa de `.env`: o default é `VITE_API_MODE=mock` — API em memória com fixtures determinísticas (seed 42), realtime simulado e mutações que emitem eventos (a UI mockada é viva, sem backend). No primeiro acesso, o onboarding pede para escolher/criar um perfil local.

Extra: `VITE_MSW=on npm run dev` serve as mesmas fixtures via HTTP `/api/v1` (msw) para inspeção no navegador.

## Apontar para a API real

```bash
# .env
VITE_API_MODE=http
VITE_API_BASE_URL=http://localhost:5000   # backend ASP.NET Core (REST /api/v1 + hub /hubs/events)
```

**O que muda:** apenas a factory `createApi()` (`src/api/index.ts`) troca `MockApiClient`→`HttpApiClient` e `MockRealtimeClient`→`SignalRRealtimeClient`. **O que NÃO muda:** nenhum componente, hook, query ou tela — o contrato (39 recursos Zod + comandos + 25 eventos) é o mesmo nos dois modos. O contrato esperado do backend está em `../docs/frontend/HANDOFF_API.md`.

## Scripts

| Script | Descrição |
| --- | --- |
| `npm run dev` | Dev server (Vite) |
| `npm run build` | Typecheck + build de produção |
| `npm run preview` | Serve o build (porta 4173) |
| `npm run check` | `lint` + `typecheck` + `test --run` (gate único) |
| `npm run lint` | ESLint (flat config) |
| `npm run typecheck` | `tsc -b` |
| `npm run test` | Vitest (watch); `-- --run` para CI |
| `npm run test:e2e` | Playwright — todos os specs, incl. a11y (build + preview automáticos) |
| `npm run test:a11y` | Só o gate de acessibilidade (axe-core em todas as rotas, 2 viewports × 2 temas) |
| `npm run storybook` | Storybook dev (porta 6006) |
| `npm run build-storybook` | Build estático do Storybook |
| `npm run format` | Prettier write |

## Estrutura

```
src/
  app/             # bootstrap, providers, router (lazy por rota), AppShell (sidebar/bottom nav/drawer)
  design-system/   # tokens.css, componentes base (+ .stories.tsx)
  api/             # camada contract-first: contracts/ (39 recursos Zod + comandos + streams),
                   # client/ (mock + http), realtime/ (mock + signalr), fixtures/ (seed 42), mocks/ (msw)
  features/        # 21 features, cada uma com components/ hooks/ pages/ __tests__/
  i18n/            # locales/pt-BR.json + en.json (base) e locales/<lang>/<feature>.json (módulos)
  lib/             # cn(), formatadores Intl, máscara de segredos
  stores/          # session-store, theme-store, active-project-store, ui-store (zustand + persist)
  config/          # product (nome, versão, codename)
e2e/               # Playwright: fluxos FE-1/2/3, shell e a11y (projetos mobile-360 e desktop-1440)
```

Documentação viva em `../docs/frontend/`: `CURRENT_STATE.md`, `SCREENS.md` (inventário de telas + estados), `HANDOFF_API.md` (contrato p/ backend), `DECISIONS.md` (D-001+), `PROGRESS.md`.

## Convenções inegociáveis

- **Tokens, nunca cores hardcoded**: componentes usam apenas as variáveis de `tokens.css` (via classes Tailwind mapeadas em `tailwind.config.js`). Tema escuro padrão; claro derivado por tokens.
- **i18n em tudo**: zero string hardcoded em JSX — garantido por teste estático (`src/i18n/__tests__/i18n-hygiene.test.ts`, AST via TypeScript) + paridade pt-BR ↔ en. Módulos novos vão em `src/i18n/locales/<lang>/<feature>.json` (namespace de topo único por arquivo).
- **Enums/estados de domínio**: sempre os literais do contrato Zod (`src/api/contracts/`), traduzidos via chaves `status.*` — nunca string solta.
- **Mobile-first**: base 360px, breakpoints `md 768 / lg 1024 / xl 1440`, alvos de toque ≥ 44px.
- **Acessibilidade WCAG 2.1 AA**: landmarks, foco visível, navegação por teclado — gate axe-core no CI (`npm run test:a11y`), zero violações critical/serious.
- **Estados de tela**: toda tela tem vazio com orientação, skeleton, erro com retry; reconexão é global no AppShell.
- **Zero dado fabricado**: campo fora do contrato aparece como "não disponível no contrato atual" e vira pendência no HANDOFF_API.
- **Branches**: `develop` é a linha principal; trabalho em branches de feature (`feature/fe-<n>-<nome>`) mergeadas em `develop`. Não commitar direto em `develop`.
