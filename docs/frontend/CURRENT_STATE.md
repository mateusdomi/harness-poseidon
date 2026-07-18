# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase em andamento:** FE-3 (Complementares) — FE-0, FE-1 e FE-2 CONCLUÍDAS (gates verdes, push feito).
- **Branch ativa:** `develop` (sincronizada com `origin/develop`; atenção: outro agente publica backend na mesma branch — sempre `git pull --no-rebase` antes de push).
- **Último marco:** gate FE-2 verde — 239 testes, lint 0 erros, build ok, 10 E2E (fe1-flow, app-shell, fe2-flows: aprovação de documento + passagem de bastão) em mobile-360 e desktop-1440.

## O que existe (FE-0 pronto)

- `frontend/`: Vite 5 + React 18 + TS strict, Tailwind 3.4, design system com tokens dark/light (dark padrão, `#0A0A0F`, violeta `#7C5CFC` → magenta `#EC4899`, CTA verde-limão `#B6FF3C`), shadcn manual (button/card/badge/input/skeleton + stories), i18n pt-BR (+en esqueleto), AppShell (sidebar colapsável lg+, barra inferior + drawer mobile), 21 features com rotas lazy placeholder.
- `src/api/` completa: 39 recursos com schemas Zod, 25 eventos tipados, 22 enums centrais, `ApiClient` (mock/http), `RealtimeClient` (mock/signalr) com dedupe + re-sync por sequence, fixtures determinísticas pt-BR (seed 42: 2 projetos, 40 tarefas, documentos, agentes, notificações etc.), factory `createApi()` por `VITE_API_MODE`, `ApiProvider` em `src/app/providers.tsx`, badge de notificações real.
- `docs/frontend/HANDOFF_API.md` criado (contrato completo consumido).
- Decisões registradas em `docs/frontend/DECISIONS.md` (D-001 a D-008).

## Próximo passo exato (FE-3 Complementares)

Implementar as telas reais restantes (substituindo placeholders), seguindo os padrões já consolidados (hooks React Query + `api`/`realtime` do context, i18n total, estados vazio/skeleton/erro, mobile-first):

1. **Histórico de conversas** (`conversations`) — filtros por projeto/período/canal/usuário, busca, renomear, arquivar (≠ excluir), abrir retomando contexto.
2. **Prototipação** (`prototypes`) — galeria por org/projeto, upload imagem/ZIP só como referência (ZIP nunca executado), cores/logo/briefing, waiver/não aplicável, versões, 3 cenários.
3. **Rodar projeto** (`run-project`) — serviços detectados, start/stop/restart, logs streaming com filtro/limpar, URLs/health checks, credenciais demo ocultas com revelar, guia "o que testar primeiro", cleanup.
4. **Providers e contas** (`providers`) — catálogo de modelos (read-only + sincronizar), contas com saúde/cota/janela/reset, budgets com barras, política de roteamento com edição guardada por confirmação.
5. **PO Assistant** (`po-assistant`) — entrada texto+anexos; painéis: requisitos, ambiguidades, contradições, perguntas, critérios de aceite; curadoria humana; "criar demanda estruturada".
6. **Licença** (`licenses`) — ativação mock, estado, entitlements, dispositivo, expiração, grace period, modo offline; pós-expiração leitura/exportação continuam.
7. **Configurações** (`settings`) — idioma, tema, diretórios, sandbox + aceite modo inseguro (persistido em FE-1a, exibir/revogar), backup/restore, diagnóstico, licença resumida, sobre.
8. Gate FE-3: E2E de rodar projeto (mock) e do PO Assistant nos 2 viewports → commit + push.

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
