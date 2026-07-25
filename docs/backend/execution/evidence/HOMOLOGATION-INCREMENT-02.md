# Homologação incremental 02 — Dashboard, Chat, Workflow, Quadro, Projetos e Entregas

Data: 2026-07-25  
Branch: `develop`  
Baseline: `786b9e9b6091681bccd627458fd8ca12daee3df9`  
Último SHA de produto deste lote: `9115fcf`  
Execução: manutenção dirigida pelo usuário; nenhum Chief, agente autônomo, card ou release foi iniciado.

## Escopo e baseline preservado

O lote ficou restrito a Dashboard, Chat, Workflow exibido no Chat, Quadro/Kanban,
Projetos/criação de projeto e Central de Entregas. O Host oficial, o banco e
`~/.harness-poseidon` foram preservados.

Já existiam e foram mantidos: shell responsivo, preferência de tema persistida,
contratos HTTP/SignalR, cards e filtros do board, vínculo de workflow por projeto,
portfólio de entregas e os estados honestos de loading/empty/error. A implementação
foi alterada apenas onde a verificação no navegador ou um gate provou lacuna.

## Alterações e decisões

### Dashboard

- “Cockpit” foi substituído por “Dashboard” em toda a apresentação.
- Dark passou a ser o default apenas na ausência de preferência salva.
- O progresso global e a distribuição de tarefas foram consolidados; visualizações
  duplicadas foram removidas.
- Indicadores mostram numerador, denominador, fonte, atualização e pendências.
- Eventos antigos são humanizados na leitura; o código bruto fica em detalhes
  técnicos.
- Foi adicionado reconciliador periódico de saúde do board: corrige somente estados
  determinísticos e audita/alerta ambiguidades.
- “Fábrica de agentes” virou “Equipe operacional”, usando somente fontes reais.

### Chat e Workflow

- Identidade pública: Bruna Magalhães, Diretora de Engenharia e Operações de IA.
- Foto local otimizada em asset administrado, avatar acessível e modal responsivo
  com foco/Escape/fallback.
- Aliases internos permaneceram intactos; mensagens históricas da liderança são
  humanizadas apenas na projeção pública.
- O workflow técnico ganhou 15 fases canônicas, artefatos esperados e 10 gates.
  A versão anterior e runs ativos não foram reescritos.
- Um run manual foi criado idempotentemente para o projeto de homologação,
  exclusivamente para validar a nova versão no Chat; nenhuma tarefa ou execução de
  liderança foi criada.

### Quadro/Kanban

- Kanban horizontal com colunas lado a lado, cabeçalhos fixos e scroll acessível.
- Ordem principal: Backlog, Pronta, Em desenvolvimento, Em revisão, Em correção,
  Testes e gates, Concluída; Bloqueada permanece transversal.
- O contrato de tarefa passou a publicar `cardType`.
- Filtros cobrem responsável, assinatura, especialidade, tipo, fase, prioridade,
  estado, período e arquivamento.
- IDs, tipo/fase, indicador conservador de tarefa travada e explicação do fluxo
  foram adicionados sem mutar o board.

### Projetos

- Cards exibem fase, progresso, responsável público, saúde, workflow, atividade,
  repositório/branch e marca a partir de dados persistidos.
- Formulário reorganizado nas dez seções solicitadas, com apenas a aba ativa
  visível, ajuda contextual e validação.
- Workflow recomendado é reconciliado quando o catálogo chega de forma assíncrona,
  sem sobrescrever escolha explícita.
- Criação real e mock agora aplicam a mesma regra de vínculo automático.
- Upload de logo usa o diretório gerenciado pelo Host; nenhuma referência a
  Downloads permanece no runtime.

### Central de Entregas

- Disclosure de fonte, atualização, cálculo, confiança e lacunas em cards/Entrega 360.
- Entrega 360 reúne objetivo, fase, workflow, marcos, riscos, bloqueios, decisões,
  previsão, documentos, tarefas, evidências, atividades e próximos passos.
- “Sem responsável” e “Sem data” têm edição persistente, validação de permissão,
  auditoria e evento realtime.
- “Todas” e “Precisa da minha atenção” derivam de regras auditáveis.
- Forecast, owner, health, Copiloto da daily e bases de métricas são apresentados em
  português; ausência de sinal continua explícita, sem números inventados.

## Dados reais usados

- Projetos, organizações, workflows/runs/fases, tarefas, attempts, documentos,
  agentes e eventos foram lidos do Host oficial em `~/.harness-poseidon`.
- Dashboard: progresso e distribuição vêm de tarefas; atividade vem de auditoria e
  streams; equipe/cotas vêm dos agentes, contas, providers e budgets publicados.
- Entregas: responsável, datas, saúde, previsão e sinais vêm do read model DEL;
  a Entrega 360 enriquece com recursos persistidos relacionados.
- Quando o provedor não informa cota ou não há evidência suficiente de previsão,
  a UI declara a ausência. Nenhum mock é apresentado como dado real.

## Gates

- `npm run check`: 81 arquivos, 644 testes, lint e typecheck verdes.
- `npm run build`: verde; apenas aviso informativo de tamanho de chunks.
- `npm run build-storybook`: verde; avisos upstream do runtime do Storybook.
- `npm run test:e2e`: 78/78 desktop e mobile.
- `npm run test:a11y`: 46/46, WCAG A/AA nas rotas e temas.
- `npm run test:e2e:real` em modo incremental: 3/3
  (`desktop-13-dark`, `tablet-light`, `mobile-360-dark`), sem console error e sem
  asset 404.
- `tools/backend/verify.sh`: verde.
- .NET: build com 0 avisos/0 erros; 583 unitários, 41 contratos, 173 integração,
  7 arquitetura, 6 recovery e 3 concorrência.
- OpenAPI republicado a partir do Host oficial; drift verde.
- `npm audit --omit=dev`: 2 ocorrências high do advisory upstream do React Router
  `GHSA-qwww-vcr4-c8h2`. O reparo oferecido exige `--force` e mudança incompatível;
  não foi aplicado.

## Evidências visuais

Diretório: `docs/backend/execution/evidence/homologation-increment-02/`

- Dashboard: dark desktop, light tablet e dark mobile.
- Chat: três viewports, perfil ampliado de Bruna e workflow canônico expandido.
- Quadro: três viewports.
- Projetos: três viewports e formulário de criação.
- Central de Entregas: três viewports e Entrega 360.

As 19 capturas foram geradas contra o Host real e inspecionadas visualmente.

## Commits

- `d553172` — Dashboard, progresso e saúde do board.
- `1cc9507` — identidade de Bruna e workflow canônico.
- `342c47c` — Kanban horizontal e contrato de tipo.
- `c656a10` — cards de projeto e criação guiada.
- `d340b57` — portfólio rastreável e configuração de planejamento.
- `be40516` — workflow recomendado assíncrono e navegação real por abas.
- `04aca18` — identidade pública e textos de Entrega 360.
- `4755d43` — OpenAPI, seed e E2E real incremental.
- `9115fcf` — drift de contrato do tipo de tarefa.

## Pendências e decisão técnica

Pendências externas:

- advisory upstream do React Router sem correção compatível;
- credenciais/contas reais de providers e respectivas cotas;
- gates que exigem ação humana, aceite e revisão independente.

Decisão: **GO técnico somente para nova homologação humana deste lote**.  
**NO-GO para produção automática** e nenhuma declaração de GNG/release foi feita.
