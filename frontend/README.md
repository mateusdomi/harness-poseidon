# Poseidon — Frontend

Frontend do **Poseidon** (Harness Poseidon): fábrica autônoma de software onde um agente chefe coordena agentes de IA que desenvolvem projetos reais.

## Stack

- React 18 + TypeScript (strict) + Vite
- Tailwind CSS v3 + tokens de design em CSS variables (`src/design-system/tokens.css`)
- Componentes base estilo shadcn/ui copiados em `src/design-system/`
- @tanstack/react-query (server state) · zustand (tema/layout/sessão) · react-router-dom
- react-hook-form + zod · react-i18next (pt-BR padrão, en) · @microsoft/signalr · msw
- Vitest + Testing Library · Playwright · Storybook · ESLint (flat) + Prettier

## Como rodar

```bash
npm install
cp .env.example .env   # VITE_API_MODE=mock (default)
npm run dev            # http://localhost:5173
```

Modo mock é o default (`VITE_API_MODE=mock`). A camada `src/api/` (contracts/client/realtime/fixtures) chega na fatia FE-0 B.

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
| `npm run test:e2e` | Playwright (faz build + preview automaticamente) |
| `npm run storybook` | Storybook dev (porta 6006) |
| `npm run build-storybook` | Build estático do Storybook |

## Estrutura

```
src/
  app/             # bootstrap, providers, router, AppShell (sidebar/bottom nav/drawer)
  design-system/   # tokens.css, componentes base (+ .stories.tsx)
  api/             # placeholder — contracts/ client/ realtime/ fixtures/ (fatia B)
  features/        # 21 features, cada uma com components/ hooks/ pages/ __tests__/ index.ts
  i18n/            # pt-BR.json (catálogo real) + en.json
  lib/             # cn(), formatadores Intl pt-BR
  stores/          # theme-store, ui-store (zustand + persist)
  config/          # product.name = "Poseidon"
e2e/               # Playwright smoke do shell (360px e 1440px)
```

## Convenções inegociáveis

- **Tokens, nunca cores hardcoded**: componentes usam apenas as variáveis de `tokens.css` (via classes Tailwind mapeadas em `tailwind.config.js`). Tema escuro padrão; claro derivado por tokens.
- **i18n em tudo**: zero string hardcoded em JSX — usar `useTranslation()`.
- **Mobile-first**: base 360px, breakpoints `md 768 / lg 1024 / xl 1440`, alvos de toque ≥ 44px.
- **Acessibilidade WCAG 2.1 AA**: landmarks, foco visível, navegação por teclado.
