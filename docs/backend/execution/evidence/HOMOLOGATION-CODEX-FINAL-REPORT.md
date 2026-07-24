# Relatório final da campanha de correção da homologação

## Declaração de papel e escopo

Execução feita por mantenedor temporário, com origem
`human_directed_maintenance`, por autorização direta do usuário. Esta campanha
não assumiu a identidade do Chief, não atribuiu decisões ao Chief, não criou
claims/attempts/reviews fictícios e não declarou GNG, homologação, Release
Candidate ou prontidão para produção.

## Baseline e estado auditado

| Fato | Valor |
|---|---|
| Baseline mínimo e `origin/develop` no início | `49188059d05ca9d0dafab54f40aac8b0bb8c5551` |
| `develop` local encontrado no início | `547a56071e93b5425d88a129696ca886ff6960eb` |
| Delta local preexistente | 111 commits à frente, zero divergência |
| SHA final do produto auditado antes do commit deste relatório | `5b4cd29d0fe92c6b179608eb70d4db70b6cf7c37` |
| Branch | `develop` |
| Banco da campanha | SQLite de desenvolvimento controlado em `.artifacts/`, nunca o banco instalado |
| RC3 do usuário | preservada e não acessada para escrita |

O SHA definitivo do repositório, que inclui este próprio arquivo e os
checksums gerados, é informado na mensagem operacional final; um documento
versionado não consegue autorreferenciar o hash do commit que o contém.

O repositório já continha a maior parte das implementações descritas pelo
feedback. A auditoria preservou o que funcionava e concentrou novas alterações
em lacunas comprovadas: disciplina auditável no board, falso positivo do
linter de governança, dependência instável do filtro do quadro, identidade
visual reaberta, Mermaid local e dois defeitos de acessibilidade que só
apareciam com dados reais. Os gates integrais também revelaram fixtures
defasadas, logs incorretos no encerramento normal de workers, uma corrida do
Mermaid sob React StrictMode e falta de isolamento entre cenários do pacote;
todos foram corrigidos sem reduzir cobertura.

## Inventário das 557 linhas da missão

As 557 linhas recebidas foram normalizadas sem descartar critérios:

| Grupo | Resultado do inventário |
|---|---|
| Identidade, Git, governança e honestidade | 1 campanha, 4 demandas, 20 tarefas; restrições preservadas |
| P0 runtime/acessibilidade/console | 5 requisitos executáveis |
| P1 Cockpit/chat/quadro/workflows/orquestração/providers | 21 requisitos executáveis |
| P2 dados/documentos/arquitetura/admin/UX | 23 requisitos numerados |
| P2 dados do Poseidon e updater | 2 requisitos transversais adicionais |
| P3 fleet e avaliação | 5 requisitos executáveis |
| Gates, critic, higiene e entrega | tratados como condições de prova, não como percentuais |
| Fonte canônica de produto | 86 entradas em `docs/product/feature-ledger.json`, auditadas por área |

O inventário executável abaixo tem 56 linhas. Ele é a projeção agrupada das 557
linhas textuais, e não uma alegação de 557 funcionalidades independentes.

## Board e cadeia oficial

Manifesto idempotente: `docs/backend/execution/homologation-campaign.json`.

Importador explícito e desabilitado por padrão:
`tools/backend/import-homologation-campaign.sh`. Ele exige URL loopback,
cookie-jar local e ULID de projeto. O verificador
`tools/backend/verify-homologation-campaign.sh` está ligado ao gate principal.

Foram reconciliados:

- 1 solicitação `HML-CAMPAIGN-2026-07-24`;
- 4 demandas P0–P3;
- 20 tarefas estáveis `HML-*`;
- origem `human_directed_maintenance`;
- instrução versionada com obrigação de auditar antes de editar;
- commits e gates anexados às transições reais das tarefas exercitadas.

No fechamento, 19/20 cards canônicos ficaram em `testsGates`: a implementação
e as provas determinísticas terminaram, mas o sistema só permite `done` depois
de um attempt concluído e esta campanha não fabricou attempts nem revisão
independente. `HML-GLOBAL-EXT` ficou `blocked`, com as credenciais, decisões de
owner e gates humanos discriminados. Um registro inicial com título antigo de
`HML-GLOBAL-001`, criado somente no banco controlado durante a primeira
reconciliação, foi preservado como superseded/blocked para não apagar
histórico; ele não integra os 20 cards do manifesto final.

## Matriz final por requisito

Legenda da classificação inicial:
`ImplementedAndWorking`, `ImplementedButBroken`, `Partial`, `Missing`,
`StaleData`, `UXOnly`, `ExternalDependency`, `HumanGate` e `NotApplicable`.

| ID | Item | Classificação inicial | Ação | Estado final | Evidência | Testes | Pendência |
|---|---|---|---|---|---|---|---|
| P0.1 | Renomear conversa | ImplementedButBroken | PATCH e store dual, erro tipado e persistência | Fixed | `6255f72`, OpenAPI `acd01bd` | unit/integration/conversations E2E | nenhuma |
| P0.2 | Detalhe do projeto | ImplementedButBroken | normalização contratual e ErrorBoundary | Fixed | `6255f72` | projeto completo/legado/vazio/parcial | nenhuma |
| P0.3 | Entregas `view=all` | ImplementedButBroken | query canônica, capability e empty state | Fixed | `6255f72` | `DeliveryApiTests`, E2E real | nenhuma |
| P0.4 | Foco em modais/overlays | ImplementedButBroken | foco restaurado; sem `aria-hidden` sobre foco | Fixed | `6255f72` | axe 46/46; E2E real | nenhuma |
| P0.5 | Console/asset/route errors | Partial | gate de console, asset HTTP, boundaries e shutdown normal sem falso erro | Completed | `watchRuntime` em `http-real.spec.ts`, `0242f31` | E2E mock/real | extensões são excluídas da telemetria |
| P1.1 | pt-BR | Partial | catálogo e valores humanizados | Completed | `9de664f`, `1a6c570` | higiene i18n 2/2 | nomes próprios/códigos permanecem técnicos |
| P1.2 | Saúde da governança | Partial | métricas com definição, impacto e CTA | Completed | `0a430f1`, `f0b40ef` | cockpit/governance unit+E2E | nenhuma |
| P1.3 | Fase atual | Partial | workflow default e convergência idempotente | Completed | `ebbae73`, `8001aad` | convergence integration | ambiguidade exige humano |
| P1.4 | Progresso global | StaleData | derivação por cards/evidências/gates | Completed | `0a430f1` | `cockpit-derive`, E2E | aceite humano não é inferido |
| P1.5 | Tarefas por estado/backlog health | Partial | DoR, vínculo obrigatório e stuck detection | Completed | `0c1dc0a`, `df234f1` | card discipline/backlog tests | nenhuma |
| P1.6 | Atividade recente | UXOnly | eventos de negócio humanizados | Completed | `0a430f1` | `activity-humanize` | técnico em disclosure |
| P1.7 | Saúde dos agentes | Partial | capacidade produtiva e realtime | Completed | `0a430f1`, `8840207` | cockpit/agents/realtime | cota ausente fica desconhecida |
| P1.8 | Visualizações operacionais | UXOnly | throughput/estado/ocupação com tabela equivalente | Completed | `8840207`, `4b4ae18` | chart unit+a11y | nenhuma |
| P1.9 | Workflow default | Partial | preseleção versionada e convergência | Completed | `0dd5f45`, `ebbae73` | project/workflow integration | nenhuma |
| P1.10 | Conta/modelo/esforço/fallback no chat | Partial | seleção real e binding até executor | Completed | `d076b07`, `3793526` | model effort mappings + chat | credencial externa pode bloquear |
| P1.11 | Quadro | Partial | ID, busca, filtros reais e estado realtime | Completed | `ca5d3eb`, `9508dd5`, `5f80162` | board 22/22 + E2E | nenhuma |
| P1.12 | Modos Manual/Semiautônomo/Autônomo | ImplementedAndWorking | auditoria de persistência/políticas | AlreadyWorking | `F3-SEMIAUTONOMOUS-WORKFLOW.md` | workflows 13/13 | autônomo nasce desligado |
| P1.13 | Cards de workflow | UXOnly | tags/fases/versão/origem | Completed | `6414cd7` | workflows unit/E2E | nenhuma |
| P1.14 | Comparação de versões | ImplementedButBroken | A/B, origem/destino e diff estrutural | Fixed | `6414cd7` | version diff 10/10 | nenhuma |
| P1.15 | Visão humana do Chief | Partial | atividade/modo/nome/saúde/rota | Completed | `52605ba`, `6fb79b9` | orchestrator 16/16 | nome não altera `agent_key` |
| P1.16 | Diagnóstico avançado | UXOnly | saúde no padrão; lease/fencing em disclosure | Completed | `52605ba` | orchestrator unit+a11y | nenhuma |
| P1.17 | Pausar/drenar/handoff | ImplementedAndWorking | auditoria de confirmação, resultado e eventos | AlreadyWorking | `F2-CHIEF-COMMANDS.md` | integration + orchestrator E2E | operação live depende de executor |
| P1.18 | Definições de agentes | Partial | seções, tags, histórico e binding acionável | Completed | `bbb23af`, `0a898d6` | CRUD/import 21 testes | nenhuma |
| P1.19 | Tela Agentes/Fleet | Partial | persona separada de identidade e avatar abstrato | Completed | `6fb79b9`, `162e5f8` | agents 27 testes+a11y | nenhuma |
| P1.20 | Autenticação de contas | ExternalDependency | probe isolado, revalidar e credentialRef | BlockedExternal | `AUTH-RESOLUTION.md`, `3809c22` | testes de isolamento/probe | logins externos não foram refeitos nesta sessão |
| P1.21 | Providers reais | Partial | registry/adapters/capabilities reais | Completed | `c9f0668`, `6f282a1` | provider CRUD/routing/readiness | Kimi precisa decisão do owner |
| P2-DATA | Dados reais do Poseidon | StaleData | convergência idempotente de projeto/workflow/front/arquitetura | Completed | `8001aad`, `344b9a3` | seed/convergence/restart | fatos não inferíveis ficam não confirmados |
| P2.1 | Documentos materiais | Partial | catálogo, versão, lifecycle, aprovação | Completed | `57e6ba9`, evidências F1/F2 | documents 16 testes + integration | nenhuma |
| P2.2 | Renderização humana | Partial | GFM + Mermaid local estrito + fallback honesto; serialização segura no StrictMode | IndependentReviewPending | `2986d4a`, `775dec3`, `MERMAID-RENDERING-EVIDENCE.md` | unit 7/7; E2E HTTP real 3/3 | critic independente |
| P2.3 | Scroll independente | UXOnly | árvore e leitor com overflow próprio | Completed | `66182fd` | governance-docs + axe | nenhuma |
| P2.4 | Explosão documental | Partial | materialidade, agrupamento e guardrail | Completed | `0c1dc0a`, `d39d077` | governance/document guard | nenhuma |
| P2.5 | Protótipo/baseline do Poseidon | StaleData | frontend reconciliado como baseline implementada | Completed | `8001aad` | convergence + prototypes | nenhuma |
| P2.6 | Arquitetura AS-IS | Missing | seeder com elementos/relacionamentos reais | Completed | `344b9a3`, `04746f8` | architecture unit/integration/E2E | revisão humana do mapa |
| P2.7 | Aprovações | ImplementedAndWorking | fluxo documento/protótipo/arquitetura/gate auditado | AlreadyWorking | `F2-APPROVAL-CENTER.md` | approvals 6/6 + integration | decisões são humanas |
| P2.8 | Objetivo da governança | UXOnly | introdução, saúde, atenção e ações humanas | Completed | `f0b40ef` | governance unit+a11y | nenhuma |
| P2.9 | Métricas de governança | Partial | tradução, período, impacto e drill-down | Completed | `f0b40ef`, `d39d077` | governance tests | nenhuma |
| P2.10 | Documentos de governança | Partial | busca/árvore/render/fonte/edição/versionamento | Completed | `66182fd`, `2986d4a` | unit + E2E real | edição continua governada |
| P2.11 | Branding | Partial | upload/preview/pickers/presets/contraste | Completed | `brand-fields.tsx`, `d92567a` | project-form + visual/a11y | aceite visual humano |
| P2.12 | Defaults de organização | Partial | workflow/defaults herdados com provenance | Completed | `0dd5f45`, organização F2 | integration | nenhuma |
| P2.13 | Canais | ExternalDependency | UI/vínculo real; Telegram comprovado | BlockedExternal | `3c5351e`, evidências F9 | channels 5/5; E2E real | smoke Teams/Entra externo |
| P2.14 | Ferramentas/skills/plugins/MCP | Partial | propósito, risco, policy, probe e estado | Completed | `f52edf7`, F2 tool policy | tools 10/10 | MCP indisponível não é marcado ativo |
| P2.15 | Licença | Partial | estado único, capability, ativação e validade | Completed | F8 signed licensing, `f52edf7` | licenses 2/2 + integration | emissão comercial é externa |
| P2.16 | Assistente de PO | Partial | propósito/empty state/CTA/capability | Completed | `f52edf7` | PO assistant 4/4 | nenhuma |
| P2.17 | Onboarding | ImplementedButBroken | continuar/revisar/reiniciar/progresso | Fixed | `f52edf7`, golden path | onboarding 18 testes + E2E | nenhuma |
| P2.18 | Notificações | Partial | categorias, canais, frequência e autosave | Completed | `d344762`, `c9a9130` | notifications 13 testes | canais externos dependem de config |
| P2.19 | Configurações/backup | Partial | diretório real, diagnóstico pt-BR, backup/restore | Completed | `eebab74`, F2 local operations | settings + recovery | restore exige confirmação |
| P2.20 | Responsividade/a11y | Partial | viewports, 200%, teclado, semântica real e contraste determinístico dos avatares | Completed | `2986d4a`, `1fb7e7b`, visual evidence | axe 46/46 + E2E real 3/3 | leitor de tela humano |
| P2.21 | Identidade visual | UXOnly | hero, gradiente, fonte local, grid, profundidade e contraste AA | IndependentReviewPending | `2e7481c`, `1fb7e7b`, oito screenshots | visual 2/2; build; axe | aceite visual do owner e critic |
| P2.22 | Navegação | Partial | grupos por frequência, recolhíveis e persistidos | Completed | `c9ff3a1`, `b0a3d6c` | app shell + E2E | nenhuma |
| P2.23 | Perfil | Missing | avatar/nome/org/locale/conta/troca | Completed | `c9ff3a1` | app shell/profile tests | logout remoto depende de auth |
| P2-UPDATER | Atualização do aplicativo | Partial | porta fixa, check/update, backup/rollback local | BlockedExternal | `4e3d11d`, `eb4b4d4` | lifecycle/package local | feed remoto assinado e Apple signing externos; nenhuma Release criada |
| P3.1 | Separação Chief/especialistas/critic | ImplementedAndWorking | política e guards auditados | AlreadyWorking | ADR-022, Chief backlog policy | agent-run/card discipline | fallback exige auditoria |
| P3.2 | Utilização da equipe | Partial | cota, especialidade, fila e scheduler | Completed | `N4-SCHEDULER-QUOTAS.md`, `5fccb61` | scheduler/concurrency/pilot | contas externas variam |
| P3.3 | Evitar colisão | ImplementedAndWorking | claims granulares/worktrees/integração | AlreadyWorking | `BACKEND-SCOPE-GRANULAR-CLAIMS.md` | concurrency/recovery | worktrees preexistentes preservadas |
| P3.4 | Avaliação de agentes | Partial | scorecards auditáveis e comparativos | Completed | commits fleet/cockpit/agents | derivação e E2E | métrica não é verdade absoluta |
| P3.5 | Capacidade insuficiente | HumanGate | recomendação explicável, sem compra automática | HumanGate | políticas de scheduler/fleet | unit/policy | contratação é decisão humana |

## Commits desta campanha

| SHA | Escopo |
|---|---|
| `3940a67` | manifesto/importador/verificador idempotente da campanha |
| `a7b2553` | linter ignora worktrees auxiliares e governança sincronizada |
| `5f80162` | dependência memoizada estável no filtro do quadro |
| `2e7481c` | identidade visual de impacto e screenshots antes/depois |
| `2986d4a` | Mermaid local, E2E real ampliado e correções a11y |
| `565e0bc` | formatação canônica restaurada no teste de arquitetura |
| `0242f31` | cancelamento normal de workers deixa de gerar falso erro crítico |
| `e063396` | fixtures de integração alinhadas aos defaults governados |
| `1fb7e7b` | contraste AA dos avatares e prova atual dos checkboxes |
| `775dec3` | renderer Mermaid serializado e seguro sob StrictMode |
| `5b4cd29` | cenários do pacote limpo isolados por banco temporário |

## Migrations, contratos, endpoints e telas

Nenhuma migration foi adicionada nesta campanha: as migrations duais
preexistentes já cobrem os domínios auditados. SQLite e PostgreSQL em Docker
foram exercitados, sem skip de infraestrutura.

Contratos OpenAPI/eventos não mudaram nesta campanha. O P0 de conversas já
estava reconciliado em `acd01bd`. Endpoints exercitados incluem:

- `PATCH /api/v1/conversations/{id}`;
- `GET /api/v1/deliveries`;
- projetos, workflow consistency e readiness;
- work board, solicitation/demand/task/instruction;
- providers, contas, modelos, routing e budgets;
- documentos, approvals, governance docs e governance runtime;
- channels, agents, orchestrator, operations e realtime.

Telas cobertas pelo E2E/a11y: onboarding, Cockpit, projetos, chat, conversas,
quadro, workflows, entregas, documentos, documentos de governança, protótipos,
arquitetura, aprovações, orquestrador, agentes, ferramentas, executar projeto,
organizações, providers, canais, assistente de PO, governança, licenças,
notificações e configurações.

## Evidência visual

Antes:

- [desktop escuro](homologation-visual/before-desktop-dark.png)
- [desktop claro](homologation-visual/before-desktop-light.png)
- [mobile escuro](homologation-visual/before-mobile-dark.png)
- [mobile claro](homologation-visual/before-mobile-light.png)

Depois:

- [desktop escuro](homologation-visual/after-desktop-dark.png)
- [desktop claro](homologation-visual/after-desktop-light.png)
- [mobile escuro](homologation-visual/after-mobile-dark.png)
- [mobile claro](homologation-visual/after-mobile-light.png)

## Gates e resultados

Resultados finais:

- `tools/backend/verify.sh`: verde no SHA `5b4cd29`; scan de segredos,
  manifesto da campanha e governança verdes;
- `dotnet format --verify-no-changes`: verde;
- build .NET Release: zero warning e zero erro;
- .NET: 808/808 — unit 578, integration 173, contract 41, architecture 7,
  recovery 6 e concurrency 3;
- PostgreSQL/Docker e SQLite: exercitados; zero skip de infraestrutura;
- frontend lint, typecheck e build: verdes;
- Vitest: 80 arquivos, 632/632;
- Storybook build: verde; apenas warnings conhecidos do bundler/SignalR;
- E2E mock: 78/78 em servidor isolado;
- axe-core: 46/46 rotas/temas;
- E2E HTTP/SignalR real: 3/3 — desktop, tablet e mobile; claro/escuro,
  Mermaid, console, assets e rotas críticas;
- identidade visual: 2/2 viewports, dois temas, com oito screenshots;
- pacote self-contained `osx-arm64`: build/publicação local verdes; 2/2
  jornadas com diretórios de dados vazios e independentes; nenhum artefato de
  Release foi criado;
- `npm audit --omit=dev`: **não verde**, 2 advisories high do React Router,
  zero critical. O único `fix` proposto pelo npm exige mudança incompatível;
  registrado como `BlockedExternal`, não como pass;
- skips: nenhum. Credenciais externas e gates humanos não foram executados e
  permanecem explicitamente bloqueados, não “skipped as pass”.

As primeiras execuções do gate integral e do pacote detectaram falhas reais.
Elas foram corrigidas e repetidas; somente os resultados finais acima são
tratados como prova autoritativa.

## Dependências externas e revisão

Bloqueios externos honestos:

- autenticação/credenciais de contas externas não foi alterada nem copiada;
- Kimi requer decisão do owner sobre adapter;
- Entra ID e Teams exigem ambiente/credencial externos;
- React Router possui advisory upstream na linha v7 sem correção compatível
  adotável nesta campanha;
- feed remoto assinado, Developer ID/notarização e teste em macOS limpo são
  externos;
- GNG-3, GNG-4 e GNG-6 são gates humanos e não foram declarados.

Não havia outro executor oficial independente disponível para revisar as novas
fatias. A identidade visual e o renderer Mermaid ficam
`IndependentReviewPending`; verificações determinísticas foram executadas, sem
fingir critic.

## Processos e higiene

O Host temporário desta campanha usou somente loopback e banco controlado e foi
reservado para encerramento após a reconciliação do board. Processos, branches e worktrees encontrados antes
da campanha não foram interrompidos nem removidos. Nenhuma branch/worktree foi
criada por esta execução. Não houve force push, merge em `main`, Release ou
alteração de segredo.

## Próximo passo humano

Após o gate final e o push fast-forward:

1. iniciar pelo launcher/CLI oficial;
2. abrir o endereço local informado pelo launcher;
3. validar visualmente Cockpit, Chat, Quadro, Entregas, Documentos,
   Arquitetura, Providers e Configurações em claro/escuro;
4. autenticar as contas que o usuário deseja exercer e revalidar os probes;
5. exercer Telegram/Teams/Entra somente com credenciais próprias;
6. registrar aceite ou findings humanos no board.

Conclusão técnica para nova homologação: **apto para uma nova rodada humana,
com os bloqueios externos e revisões independentes acima visíveis**. Isto não é
aceite de produção, GNG ou homologação.
