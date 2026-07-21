# GP-C3 — eventos reais do ciclo de vida do turno

Data: 2026-07-21. Branch: `develop`.

Terceira parte da Fatia C (ADR-019). Os eventos declarados na Fatia A deixam de ser apenas
nomes no catálogo: passam a ser emitidos nas transições reais, com payload publicado.

## Causa raiz

`docs/contracts/events.json` listava `readiness.changed`, `execution.blocked`,
`execution.enqueued`, `message.received`, `model.responded`, `provider.invoked` e
`turn.registered`, mas o catálogo publicava **somente nomes**: nenhum publisher emitia e
nenhum payload era especificado. O frontend registrou a lacuna em `HANDOFF_API.md` §9.1.2 e
usou schemas permissivos para não inventar campos. Além disso `chief.turnStateChanged`
estava no catálogo desde antes e nunca havia sido emitido.

## Entrega

Emissão nas transições reais, sempre dentro da mesma transação do estado que a originou —
ledger (auditoria) e Outbox (realtime) juntos, nunca best-effort:

| Transição | Eventos emitidos |
|---|---|
| Enfileiramento do turno | `message.received`, `turn.registered`, `execution.enqueued`, `chief.turnStateChanged` (`pending`) |
| Recusa por prontidão | `execution.blocked` (Fatia C2) |
| Aquisição do lease | `provider.invoked`, `chief.turnStateChanged` (`processing`) |
| Conclusão | `chat.turnStarted/turnChunk/turnCompleted`, `message.appended`, `model.responded`, `chief.turnStateChanged` (`completed`), `demand.created` |
| Falha/retry | `chief.turnStateChanged` (`failed` ou `pending`, com `errorCode`) |

- **`provider.invoked`** é emitido na aquisição do lease — o momento em que o turno passa ao
  provider real — com conta, modelo, effort, valor de effort do provider e número da
  tentativa.
- **`model.responded`** é distinto de `chat.turnCompleted`: o primeiro é a resposta do
  modelo, o segundo a conclusão de transporte. Essa separação é o núcleo do ADR-019.
- Payloads **sanitizados**: apenas identificadores e seleção. Nunca conteúdo de prompt,
  texto da resposta, credencial ou referência de segredo. O teste assere explicitamente que
  `model.responded` não carrega `content`.
- Paridade dual-provider: SQLite (dispatcher único) e PostgreSQL (advisory locks) emitem a
  mesma sequência.
- Payloads publicados em `docs/contracts/events.json` (versão **1.2**), com schema por evento
  — campos obrigatórios e tipos —, atendendo ao pedido do frontend (§9.1.2).

## Prova

`ConversationApiTests.ConversationMessagesAndTurnAreTenantScopedDurableAndSequenced`
comprova a **ordem exata** dos 15 eventos do ciclo e a **sequência contígua 1..15** no stream
da conversa, sem lacuna nem duplicata, além de reinício do Host com snapshot/delta
recuperados e replay sem duplicação. O payload de `model.responded` é verificado como
sanitizado.

`EventCatalogContractTests` passou a exigir que todo evento do golden path tenha payload
publicado (objeto com `required` e `properties` não vazios) e que todo payload publicado
exista no catálogo tipado — o drift continua garantindo paridade.

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral: **320** testes verdes — 177 unit, 95 integration, 31 contract,
  7 architecture, 6 recovery, 3 concurrency.
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Handoff frontend

`events.json` subiu para **1.2** com a seção `payloads`. Os schemas permissivos
(`z.object({}).passthrough()`) podem virar tipados. `chief.turnStateChanged` agora é
emitido e carrega `state` do conjunto fechado
(`pending|processing|completed|failed|blocked`).
