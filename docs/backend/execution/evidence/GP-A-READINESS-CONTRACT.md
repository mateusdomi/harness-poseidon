# GP-A — prontidão canônica do golden path (contrato, API e eventos)

Data: 2026-07-21. Branch: `develop`.

Fatia A do fechamento do golden path. Publica **primeiro** o contrato e os eventos,
conforme o handoff desta rodada (`docs/backend/execution/GOLDEN_PATH_HANDOFF.md`).
Entrega aditiva: nenhum comportamento existente foi alterado.

## Entrega

- Módulo `Harness.Modules.Readiness` com enums fechados `ConfigurationState`
  (`Unconfigured|Simulated|Configured|Ready|Degraded|Unavailable`) e `ReadinessStep`
  nas nove etapas canônicas (`ProfileReady` → `ExecutionReady`), mais os contratos
  `ProjectReadinessSnapshot`, `ReadinessStepContract`, `ReadinessBlocker` e
  `ReadinessNextAction` (ADR-017).
- `ReadinessEvaluator` é puro e determinístico: não faz IO, não persiste estado e não
  cria autoridade de domínio. Mapeia fatos coletados para estado, bloqueadores tipados,
  próxima ação, capability, `executionMode` e `messageCode`. O domínio nunca emite texto
  livre voltado ao usuário — apenas códigos localizáveis.
- `ProjectReadinessService` coleta os sinais reais somente-leitura de organizações,
  catálogo de providers, catálogo de agentes e vínculos de workflow.
- `GET /api/v1/projects/{projectId}/readiness` publica o snapshot, com 400 para ULID
  inválido, 401 sem sessão local e 404 para projeto inexistente.
- Precondição de organização (S6.1): sem organização, `ProjectReady` retorna bloqueador
  `organization.required` e ação `organization.create` — nunca um formulário aparentemente
  preenchível.
- Enquanto o auto-seed de conveniência da RC3 existir (removido na Fatia B, ADR-018), as
  contas e modelos semeados são reportados como `Simulated`, e não como configuração real
  do usuário. Assim a prontidão já é honesta antes mesmo da remoção do seed.
- Sete tipos de evento declarados no `EventTypeCatalog` e em `docs/contracts/events.json`
  para consumo do frontend (emissão nas transições entra nas Fatias B/C, ADR-019):
  `readiness.changed`, `message.received`, `turn.registered`, `execution.enqueued`,
  `provider.invoked`, `model.responded`, `execution.blocked`.

## Defeito encontrado e corrigido nesta fatia

Um Chief presente e com modelo resolvível, porém **não saudável**, fazia `ExecutionReady`
cair em "não pronta, nenhum bloqueador, inicie uma conversa": estado contraditório que
convidaria o usuário a conversar com um agente indisponível, sem explicar o motivo.
Corrigido: o caso passa a produzir o bloqueador `agent.degraded`, estado `Degraded` e ação
`agent.recover`. Coberto por teste.

## Decisão de semântica registrada

Conta ativa é `Configured`, não `Ready`: o usuário a configurou, mas a credencial só é
comprovada por invocação real (a saúde nasce `unknown` e só muda por PATCH). Logo o
elo mais fraco de uma instalação totalmente configurada é `Configured`, mesmo com a
execução liberada — leitura conservadora, coerente com o fail-closed da missão.

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: sem alterações pendentes.
- `Harness.UnitTests`: 177/177 verdes, incluindo 14 casos novos de
  `ReadinessEvaluatorTests` (ordem canônica, precondição de organização, dado simulado
  nunca apresentado como real, execução pronta só com provider+model+workflow+chief reais,
  Chief degradado, elo mais fraco, dedupe de `nextActions` e formato dos `messageCode`).
- `Harness.ContractTests`: 31/31 verdes, incluindo `OpenApiContractTests` (determinismo do
  OpenAPI publicado contra o Host real), `EventCatalogContractTests` (events.json ==
  catálogo tipado) e o novo `ReadinessContractDriftTests`.
- `tools/backend/export-contracts.sh`: `docs/contracts/openapi.json` republicado com a rota
  e os quatro schemas de prontidão.
- Governança: `generate` → `sync` (até `changed=0`) → `lint --warnings-as-errors` verde.

`ReadinessContractDriftTests` assere deliberadamente **apenas o lado backend**. A metade
frontend (tipos TypeScript) é adicionada pelo commit frontend desta rodada e reconciliada
na rodada seguinte — ver `GOLDEN_PATH_HANDOFF.md` §4. Nenhum arquivo em `frontend/**` ou
`docs/frontend/**` foi modificado.

## Não incluído nesta fatia (staged)

Fatia B (remoção do seed fail-closed, ADR-018) e Fatia C (Chief real × confirmação de
transporte, ADR-019) seguem como commits próprios. Nenhuma Release Candidate é gerada
antes do commit frontend desta rodada.
