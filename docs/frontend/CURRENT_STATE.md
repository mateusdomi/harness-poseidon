# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase em andamento:** FE-0 (Fundação)
- **Branch ativa:** `develop` (publicada em `origin`)
- **Último marco:** bootstrap do repositório (commit em `main`, `develop` criada a partir de `main`).

## O que existe

- `README.md`, `.gitignore` na raiz.
- `docs/frontend/` com `CURRENT_STATE.md`, `PROGRESS.md`, `DECISIONS.md`.
- Ainda **não existe** `frontend/`, `HANDOFF_API.md` nem `SCREENS.md`.

## Próximo passo exato

1. Criar `frontend/` com Vite + React + TypeScript strict e a stack congelada da seção 4 do prompt de missão (Tailwind, shadcn/ui, React Query, Zustand, React Router, react-hook-form + Zod, react-i18next, @microsoft/signalr, MSW, Vitest + Testing Library, Playwright, Storybook, ESLint + Prettier).
2. Implementar tokens/tema claro-escuro da seção 5, AppShell responsivo e roteamento com rotas placeholder de todas as features.
3. Implementar a camada `src/api/` completa (contracts Zod, ApiClient + Http + Mock, RealtimeClient + SignalR + Mock, fixtures pt-BR com seed fixa).
4. Gate FE-0: `npm run check` verde, Storybook navegável, shell navegando entre rotas nos dois breakpoints → commit + push de `develop`.

## Como validar

```bash
cd frontend
npm install
npm run check   # lint + type + test
npm run dev     # modo mock (VITE_API_MODE=mock)
```
