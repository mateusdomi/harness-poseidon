# ADR-017 — Prontidão canônica e estado de configuração fail-closed

- Status: aceito
- Data: 2026-07-21

## Contexto

A homologação humana da RC3 mostrou que o produto apresenta telas e CRUDs que
parecem operacionais antes de as dependências reais existirem: um projeto pode
ser iniciado sem organização, o chat aceita mensagem sem workflow, e o
Orquestrador exibe conta "OpenAI account", modelo "GPT-5" e orçamento de US$ 100
sem que o usuário tenha configurado qualquer conta. A causa raiz é dupla: (1) não
há um modelo de prontidão canônico — cada tela infere prontidão pela ausência de
dados desconectados; e (2) dados semeados de conveniência são apresentados como
reais (ver ADR-018).

Este ADR define **como o backend expõe prontidão** de forma tipada e como
qualifica cada dependência com um estado de configuração fechado, para que
nenhum frontend precise inferir prontidão indiretamente.

## Decisão

### 1. Read model de prontidão do projeto

O backend expõe um read model tipado, `ProjectReadinessSnapshot`, com uma etapa
por dependência do golden path, na ordem canônica:

```
ProfileReady → OrganizationReady → ProjectReady → ProviderAccountReady
→ ModelReady → WorkflowReady → ChiefDefinitionReady → AgentPoolReady
→ ExecutionReady
```

Cada etapa (`ReadinessStep`) retorna:

- `state`: um valor de `ConfigurationState` (enum fechado, item 2);
- `blockers[]`: lista tipada com `code` (não texto livre) e `relatedIds`;
- `nextAction`: `{ code, route, resourceId? }` — ação/rota recomendada;
- `capability`: feature/capability associada;
- `executionMode`: `unconfigured | simulated | real` — modo de execução vigente;
- `messageCode`: código de mensagem localizável; **o domínio nunca emite texto
  livre voltado ao usuário**. A tradução vive no catálogo versionado do frontend
  e em `docs/contracts` (ver roteiro de handoff).

O snapshot agrega `overallState` (o menor estado na ordem canônica) e
`nextActions[]` deduplicadas. A etapa `ExecutionReady` só é `Ready` quando todas
as anteriores estão `Ready` ou `Configured` e existe binding verificável de
provider+model+workflow.

### 2. Estado de configuração fechado

Toda dependência que possa parecer operacional é qualificada por um enum fechado:

```
Unconfigured  — não existe / nunca configurado
Simulated     — existe apenas como dado de demonstração, marcado (ADR-018)
Configured    — configurado pelo usuário, ainda não verificado em execução real
Ready         — configurado e verificável (ex.: modelo habilitado com effort mapeado
                 e conta ativa compatível)
Degraded      — configurado porém com sinal de saúde reduzido
Unavailable   — configurado porém indisponível (ex.: credencial ausente/revogada)
```

Regras invioláveis derivadas (fail-closed) — ver ADR-018 para a remoção do seed:

- nenhuma conta de provider fictícia, nenhum orçamento fictício, nenhum modelo
  "em uso" sem invocação/config real, nenhum provider "saudável" sem credencial;
- um Chief só é `Ready` quando provider+model+workflow resolvem;
- `Simulated` só é atingível sob configuração explícita de demo/dev e sempre
  carrega identificação visual (o read model expõe `executionMode=simulated`).

### 3. Exposição

- Snapshot: `GET /api/v1/projects/{projectId}/readiness`.
- Prontidão anterior ao projeto (perfil/organização) é derivável do mesmo
  contrato com `projectId` nulo nas etapas ainda não alcançadas.
- Mudanças de prontidão publicam o evento `readiness.changed` no stream do
  projeto, com o `overallState` e os `code` das etapas alteradas (payload
  sanitizado, sem segredo).

O read model é **somente leitura e computado**: ele não cria autoridade de
domínio nem persiste estado próprio; deriva de perfis, organizações, projetos,
catálogo de providers, catálogo de agentes e workflows já existentes.

## Consequências

- O frontend deixa de inferir prontidão por ausência de dados: consome um
  contrato único, com blockers e próxima ação tipados por código.
- Precondições (projeto exige organização; turno exige workflow/Chief real)
  passam a ter representação uniforme e deep-link de retorno via `nextAction`.
- Habilita os gates do golden path (E2E sem demo) a asserir blockers explícitos
  e ausência de estados simulados apresentados como reais.
- Custo: um novo módulo de leitura e um contrato adicional em `docs/contracts`;
  o frontend precisa publicar os tipos TypeScript correspondentes antes de a
  metade frontend do teste de drift ser habilitada (ver roteiro de handoff).
