# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase em andamento:** FE-4 (Qualidade e handoff) — FE-0 a FE-3 CONCLUÍDAS (gates verdes, push feito). Todas as 21 features estão implementadas.
- **Branch ativa:** `develop` (sincronizada com `origin/develop`; atenção: outro agente publica backend na mesma branch — sempre `git pull --no-rebase` antes de push; `git add` apenas de `frontend/`, `docs/frontend/` e `.gitignore`, nunca `git add -A` por causa dos artefatos de build do backend).
- **Último marco:** gate FE-3 verde — 268 testes, lint 0 erros, build ok, 14 E2E (rodar projeto + PO Assistant) em mobile-360 e desktop-1440.

## O que existe (FE-0 pronto)

- `frontend/`: Vite 5 + React 18 + TS strict, Tailwind 3.4, design system com tokens dark/light (dark padrão, `#0A0A0F`, violeta `#7C5CFC` → magenta `#EC4899`, CTA verde-limão `#B6FF3C`), shadcn manual (button/card/badge/input/skeleton + stories), i18n pt-BR (+en esqueleto), AppShell (sidebar colapsável lg+, barra inferior + drawer mobile), 21 features com rotas lazy placeholder.
- `src/api/` completa: 39 recursos com schemas Zod, 25 eventos tipados, 22 enums centrais, `ApiClient` (mock/http), `RealtimeClient` (mock/signalr) com dedupe + re-sync por sequence, fixtures determinísticas pt-BR (seed 42: 2 projetos, 40 tarefas, documentos, agentes, notificações etc.), factory `createApi()` por `VITE_API_MODE`, `ApiProvider` em `src/app/providers.tsx`, badge de notificações real.
- `docs/frontend/HANDOFF_API.md` criado (contrato completo consumido).
- Decisões registradas em `docs/frontend/DECISIONS.md` (D-001 a D-008).

## Próximo passo exato (FE-4 Qualidade e handoff)

1. **Acessibilidade**: rodar axe (via `@axe-core/playwright`) em TODAS as páginas/rotas nos dois temas; corrigir violações sérias; verificar contraste AA dos tokens nos dois temas.
2. **Estados auditados tela a tela**: vazio (com orientação), skeleton, erro com retry, banner de reconexão, permissão negada — auditar as 21 features e completar o que faltar.
3. **Code splitting**: confirmar lazy por rota; otimizar chunk principal >500 kB (manualChunks para zod/signalr/react-markdown se simples).
4. **Sweep i18n**: criar teste automatizado que FALHA se detectar literal de texto em JSX fora de allowlist; corrigir o que aparecer; garantir paridade de chaves pt-BR/en.
5. **Docs finais**: completar `docs/frontend/HANDOFF_API.md` (100% do contrato consumido, telas reais que usam cada endpoint), criar `docs/frontend/SCREENS.md` (inventário de todas as telas com rota, dados consumidos, eventos assinados e estados), finalizar `frontend/README.md` (execução modo mock + integração com API real via VITE_API_MODE=http).
6. Gate FE-4 final: `npm run check`, `npm run build`, `npm run test:e2e`, `npm run build-storybook` todos verdes → commit + push de `develop`. NÃO fazer merge em `main`.

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
