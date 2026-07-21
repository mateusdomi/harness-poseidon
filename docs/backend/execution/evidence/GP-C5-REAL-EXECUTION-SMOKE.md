# GP-C5 — smoke condicional da execução real do Chief

Data: 2026-07-21. Branch: `develop`.

Quinta parte da Fatia C (ADR-019). Publica o gate que exerce o golden path com provider e
modelo **reais**, e torna explícita a ausência de prova quando não há credencial.

## Entrega

`GoldenPathRealExecutionSmokeTests` percorre, contra um Host real e um banco vazio:

perfil → organização → projeto → provider real → conta (nasce desabilitada, ativada por
PATCH) → modelo real com `effortMappings` → workflow recomendado → prontidão → conversa
primária idempotente → turno.

Asserções centrais:

- a prontidão canônica reconhece a configuração real: `ExecutionReady` fica `Ready` com
  `executionMode=real` (nem `unconfigured`, nem `simulated`);
- com provider e modelo reais o turno é **enfileirado** (`state=pending`) e **sem
  bloqueadores** — comprovando que o bloqueio da Fatia C2 é consequência da falta de
  configuração, não um defeito.

## Credenciais e segurança

Ativado por `HARNESS_RUN_REAL_GOLDEN_PATH=true` mais `HARNESS_SMOKE_PROVIDER_KIND`,
`HARNESS_SMOKE_CREDENTIAL_REFERENCE` e `HARNESS_SMOKE_MODEL_NAME` (opcionais:
`HARNESS_SMOKE_CONTEXT_WINDOW`, `HARNESS_SMOKE_EFFORT`,
`HARNESS_SMOKE_PROVIDER_EFFORT_VALUE`).

Nenhum segredo é lido de argumento de comando, fixture ou documentação — apenas do ambiente
do processo — e o que trafega para a API é a **referência opaca** de credencial
(`keychain://`, `dpapi://`, `secret://`), nunca o segredo em si, que jamais entra em log,
payload ou evidência.

## Sem credencial: ausência de prova declarada

Sem as variáveis, o teste registra `SKIPPED_EXTERNAL_CREDENTIALS` e retorna. **Isso não é
prova da integração real** e não deve ser lido como tal: é o registro explícito de que a
verificação externa não foi executada. A automação determinística com executor fake
permanece separada e sempre rotulada (`Harness:AgentExecutors:Mode=simulated`).

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral: **320** testes verdes — 177 unit, 96 integration, 31 contract,
  7 architecture, 6 recovery, 3 concurrency. O smoke real contou como
  `SKIPPED_EXTERNAL_CREDENTIALS` nesta execução.
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Limite honesto desta fatia

O smoke comprova a cadeia até o **enfileiramento com binding real** (conta, modelo, effort e
prontidão `real`). A continuação — resposta do modelo, demanda, tarefas, evidências, critic e
gate — depende de um executor real ligado (`CodexCliAgentExecutor`/OMP RPC) com credencial
resolvível pelo Host, que é dependência externa não disponível nesta sessão. Esse trecho
permanece **não comprovado** e está registrado como risco externo, não como verde.
