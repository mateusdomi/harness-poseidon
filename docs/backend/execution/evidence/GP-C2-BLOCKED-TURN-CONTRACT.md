# GP-C2 — turno bloqueado é estado tipado, não erro de requisição

Data: 2026-07-21. Branch: `develop`.

Segunda parte da Fatia C (ADR-019). Substitui o `400 invalid_chief_invocation_selection`
por um estado de turno tipado, durável e auditável.

## Causa raiz

`POST /api/v1/conversations/{id}/turns` resolvia a invocação **antes** de persistir qualquer
coisa. Sem provider/modelo a resolução lançava e o endpoint devolvia `400`. Três problemas:
a mensagem humana era descartada; um estado legítimo do produto (execução ainda não
configurada) era reportado como erro de requisição do cliente; e a UI não tinha bloqueadores
nem próximas ações para agir — só um texto de erro.

## Entrega

- `ChatTurnHandle` passou a carregar estado tipado: `turnId`, `conversationId`, `state`,
  `correlationId`, `readiness` (`overallState`/`executionState`), `blockers[]`,
  `nextActions[]` e `links` (prontidão e conversa). `state` segue a nomenclatura do domínio:
  `pending` (registrado/enfileirado), `processing`, `completed`, `failed` e `blocked`.
- O endpoint consulta a **prontidão canônica** (ADR-017) antes de executar. Quando
  `ExecutionReady` não autoriza, a mensagem humana é persistida e o turno é registrado como
  bloqueado — resposta `202` com `state=blocked`, bloqueadores e próximas ações do read model.
  Nenhuma resposta do Chief é fabricada.
- `400` ficou reservado a request estruturalmente inválido (`invalid_chat_turn`,
  ULID inválido); `409` continua para conflito de conversa.
- Corrida entre ler a prontidão e resolver a invocação é tratada como bloqueio tipado
  (`chief.model_unresolved`), nunca como `400` nem como resposta simulada.
- Novo agregado durável `chief_turn_blocks` (migrations duais `0048`): um turno recusado
  **não** é item de mailbox — o mailbox continua sendo a fila do que deve executar. O bloqueio
  guarda estado de prontidão, bloqueadores, próximas ações e correlação, com bloqueadores
  tipados por código (nunca texto livre de domínio).
- `IChiefTurnStore.BlockAsync` grava, na mesma transação: mensagem humana, linha de bloqueio,
  Inbox (idempotência), ledger e Outbox — com os eventos `message.appended` e
  `execution.blocked`. Implementado em SQLite (dispatcher único) e PostgreSQL (advisory lock
  por mensagem).
- Idempotência por mensagem humana (`UNIQUE (tenant_id,user_message_id)`): o retry do mesmo
  envio devolve o mesmo bloqueio, sem duplicar mensagem, registro ou evento.

## Efeito colateral correto: workflow passa a ser exigido

Como o gate do turno é `ExecutionReady`, e `WorkflowReady` faz parte da prontidão, um projeto
sem workflow deixa de aceitar turno silenciosamente — exatamente o defeito "chat aceita
mensagem sem workflow" apontado na homologação. As fixtures que exercitam execução real
passaram a declarar esse pré-requisito por `WorkflowTestBinding.BindRecommendedAsync`.

## Prova

`EmptyInstallFailClosedTests` foi estendido: numa instalação vazia, criar conversa continua
permitido (criar conversa não é executar) e o turno responde `202` com `state=blocked`,
bloqueadores não vazios, próximas ações, correlação e link de prontidão. A mensagem humana
fica persistida (`GET /messages?conversationId=` devolve exatamente uma).

`ConversationApiTests`, `ChiefDemandMaterializationTests`, `DogfoodPipelineTests` e
`PostgresServerModeHostTests` continuam exercitando o turno completo com workflow vinculado,
em SQLite e PostgreSQL.

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral: **320** testes verdes — 177 unit, 95 integration, 31 contract,
  7 architecture, 6 recovery, 3 concurrency.
- `tools/backend/export-contracts.sh`: OpenAPI republicado com os cinco schemas do turno.
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Handoff frontend

O frontend alinhou a UI ao `400` anterior (registrado em `docs/frontend/HANDOFF_API.md`
§9.1.1). Com o C2 o contrato mudou: **turno bloqueado agora responde `202` com
`state="blocked"`**. A UI deve ler `state`, `blockers[]` e `nextActions[]` em vez de tratar
`400` como bloqueio. `GOLDEN_PATH_HANDOFF.md` registra o contrato completo.

## Restante da Fatia C

C3 — emitir os demais eventos de ciclo de vida (`message.received`, `turn.registered`,
`execution.enqueued`, `provider.invoked`, `model.responded`) e `chief.turnStateChanged`.
`execution.blocked` já é emitido por esta fatia. C4 — primeira conversa idempotente.
