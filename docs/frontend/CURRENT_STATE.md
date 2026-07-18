# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase em andamento:** NENHUMA — FE-0 a FE-4 CONCLUÍDAS (DoD do frontend atendido). Todas as 21 features implementadas e os 4 comandos do gate verdes.
- **Branch ativa:** `develop` (sincronizada com `origin/develop`; atenção: outro agente publica backend na mesma branch — sempre `git pull --no-rebase` antes de push; `git add` apenas de `frontend/`, `docs/frontend/` e `.gitignore`, nunca `git add -A` por causa dos artefatos de build do backend).
- **Último marco:** gate FE-4 verde (2026-07-18) — `npm run check` (lint 0 erros/0 warnings, typecheck, 270 testes), `npm run build` (chunk principal 223 kB após manualChunks), `npm run test:e2e` (56: 14 fluxos + 42 a11y axe em 21 rotas × 2 viewports × 2 temas), `npm run build-storybook`. Novidades FE-4: `e2e/a11y.spec.ts` + script `test:a11y`, skeletons com `role="status"`, estados vazios de providers + retry de templates em organizations, sweep i18n estático + paridade pt-BR/en (`src/i18n/__tests__/i18n-hygiene.test.ts`), `docs/frontend/SCREENS.md` criado, HANDOFF_API/README atualizados, decisões D-049 a D-052.

## O que existe (FE-0 pronto)

- `frontend/`: Vite 5 + React 18 + TS strict, Tailwind 3.4, design system com tokens dark/light (dark padrão, `#0A0A0F`, violeta `#7C5CFC` → magenta `#EC4899`, CTA verde-limão `#B6FF3C`), shadcn manual (button/card/badge/input/skeleton + stories), i18n pt-BR (+en esqueleto), AppShell (sidebar colapsável lg+, barra inferior + drawer mobile), 21 features com rotas lazy placeholder.
- `src/api/` completa: 39 recursos com schemas Zod, 25 eventos tipados, 22 enums centrais, `ApiClient` (mock/http), `RealtimeClient` (mock/signalr) com dedupe + re-sync por sequence, fixtures determinísticas pt-BR (seed 42: 2 projetos, 40 tarefas, documentos, agentes, notificações etc.), factory `createApi()` por `VITE_API_MODE`, `ApiProvider` em `src/app/providers.tsx`, badge de notificações real.
- `docs/frontend/HANDOFF_API.md` criado (contrato completo consumido).
- Decisões registradas em `docs/frontend/DECISIONS.md` (D-001 a D-008).

## Próximo passo exato

FE-4 concluída — todos os itens (axe, estados, code splitting, i18n sweep, docs, gate) entregues e verdes. Resta apenas **commit + push de `develop`** (a sessão FE-4 não commitou por instrução explícita). Depois disso o frontend está pronto para integração com o backend real (`VITE_API_MODE=http` + `VITE_API_BASE_URL`), cujo contrato esperado está em `HANDOFF_API.md`. NÃO fazer merge em `main`.

## Regras permanentes

- Estados obrigatórios em toda tela: vazio (com orientação), skeleton, erro com retry, banner de reconexão, permissão negada.
- Zero string/cor/status hardcoded (tokens + i18n + enums). Testes de componente para componente com lógica.
- Commits pequenos em `develop`; push após gate verde; NUNCA merge em `main` sem autorização explícita.

## Como validar

```bash
cd frontend
npm install && npm run check   # lint + type + test
npm run dev                    # modo mock
npm run test:e2e               # Playwright
```
