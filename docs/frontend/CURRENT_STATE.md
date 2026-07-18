# CURRENT_STATE — Frontend Harness Poseidon

> Arquivo vivo de retomada. Qualquer sessão deve ler isto primeiro.

## Estado atual

- **Fase em andamento:** FE-1 (Núcleo) — FE-0 CONCLUÍDA (gate verde, push feito).
- **Branch ativa:** `develop` (sincronizada com `origin/develop`, HEAD = `d3f847e`).
- **Último marco:** gate FE-0 verde — `npm run check` (100 testes, 0 erros lint), `npm run build`, `build-storybook`, smoke E2E (mobile-360 + desktop-1440) todos verdes.

## O que existe (FE-0 pronto)

- `frontend/`: Vite 5 + React 18 + TS strict, Tailwind 3.4, design system com tokens dark/light (dark padrão, `#0A0A0F`, violeta `#7C5CFC` → magenta `#EC4899`, CTA verde-limão `#B6FF3C`), shadcn manual (button/card/badge/input/skeleton + stories), i18n pt-BR (+en esqueleto), AppShell (sidebar colapsável lg+, barra inferior + drawer mobile), 21 features com rotas lazy placeholder.
- `src/api/` completa: 39 recursos com schemas Zod, 25 eventos tipados, 22 enums centrais, `ApiClient` (mock/http), `RealtimeClient` (mock/signalr) com dedupe + re-sync por sequence, fixtures determinísticas pt-BR (seed 42: 2 projetos, 40 tarefas, documentos, agentes, notificações etc.), factory `createApi()` por `VITE_API_MODE`, `ApiProvider` em `src/app/providers.tsx`, badge de notificações real.
- `docs/frontend/HANDOFF_API.md` criado (contrato completo consumido).
- Decisões registradas em `docs/frontend/DECISIONS.md` (D-001 a D-008).

## Próximo passo exato (FE-1 Núcleo)

Implementar as telas reais substituindo placeholders, consumindo `ApiClient`/`RealtimeClient` via `ApiProvider` (NUNCA fetch/SignalR direto):

1. **Onboarding/Perfil local** — primeiro uso (nome, idioma, tema, diretório, aceite "modo inseguro" persistido e visível), seleção de perfil no retorno.
2. **Organizações** — lista + detalhe (marca herdável, políticas, projetos; criar/editar no mock; herança vs sobrescrita clara).
3. **Projetos** — lista com busca/filtros; criação/edição em abas (Identificação, Repositório, Tecnologias, Marca, Pessoas); campos versionados sinalizados; estado + última atividade no card.
4. **Cockpit** — fase atual, progresso 3 trilhas (executado/validado/aprovado separados), próxima ação "Executar no chat", bloqueios, aprovações pendentes, contadores clicáveis, saúde de agentes, cotas, atividade recente.
5. **Chat** — streaming via `chat.turnChunk`, markdown, anexos, "chefe coordenando", ações rápidas, seletor modelo/esforço, chips de referência cruzada.
6. **Quadro (Kanban)** — 8 colunas, cards movidos por eventos em tempo real (animação discreta 120–220 ms), detalhe (drawer desktop / página mobile), ações humanas, tooltip "humano não cria tarefa".
7. Gate FE-1: E2E Playwright "criar projeto → conversar → chefe cria demanda/tarefas (simulado) → cards se movem por eventos → aprovar gate" em viewport mobile E desktop → commit + push.

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
