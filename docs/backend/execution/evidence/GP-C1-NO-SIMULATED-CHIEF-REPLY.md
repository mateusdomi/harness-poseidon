# GP-C1 — executor simulado deixa de participar do pacote normal

Data: 2026-07-21. Branch: `develop`.

Primeira parte da Fatia C (ADR-019). Remove a possibilidade de o pacote de homologação
normal apresentar uma resposta fabricada como resposta real do Chief.

## Causa raiz

`HostApplication` registrava `IAgentExecutor → FakeAgentExecutor` **incondicionalmente**, em
qualquer modo. O worker do Chief (`ChiefTurnBackgroundService`) consumia esse executor, e o
texto fixo do Fake — "Recebi sua mensagem. O turno foi registrado de forma durável para: …" —
era persistido e exibido como a resposta do Chief. O executor real (`CodexCliAgentExecutor`)
só era alcançável pelo caminho separado de tentativa isolada, nunca pelo chat: mesmo com
`Harness:IsolatedExecution` em `Docker`, o turno de conversa continuava simulado.

## Entrega

- A seleção do executor passa a ser guiada por modo. O `FakeAgentExecutor` só é registrado
  sob configuração explícita: `Harness:Demo:Enabled=true` ou
  `Harness:AgentExecutors:Mode=simulated`.
- Fora desses modos o Host registra `UnavailableAgentExecutor`, que **nunca produz texto**:
  lança `AgentExecutorUnavailableException`. Falhar de forma auditável é honesto; devolver
  uma confirmação de transporte disfarçada de resposta de modelo não é.
- A automação determinística continua usando o Fake, agora sempre **rotulada**: as oito
  fixtures que exercitam turnos de chat passaram a declarar
  `--Harness:AgentExecutors:Mode simulated` no build do Host.

## Prova

`EmptyInstallFailClosedTests.NormalPackageHasNoSimulatedAgentExecutor` comprova que um Host
construído com a configuração do pacote normal resolve `UnavailableAgentExecutor`, e que o
mesmo Host com `--Harness:AgentExecutors:Mode simulated` resolve `FakeAgentExecutor`.

Combinado com a Fatia B, o efeito no golden path é: sem conta/modelo reais o
`ChiefInvocationRoutingService` já bloqueia antes de enfileirar o turno; e mesmo que
provider e modelo estejam configurados, nenhum texto simulado pode ser apresentado como
resposta de modelo no pacote normal.

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral: **319** testes verdes — 177 unit, 95 integration, 31 contract,
  7 architecture, 6 recovery, 3 concurrency.
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Não incluído (restante da Fatia C)

- **C2** — resposta bloqueada tipada: hoje a recusa de invocação ainda retorna
  `400 invalid_chief_invocation_selection`. Falta devolver um contrato com `blockers[]` e
  `nextActions[]` derivados do read model de prontidão, para que a UI distinga "bloqueado"
  de "erro de requisição".
- **C3** — emissão dos eventos de ciclo de vida já declarados no catálogo
  (`message.received`, `turn.registered`, `execution.enqueued`, `provider.invoked`,
  `model.responded`, `execution.blocked`) e de `chief.turnStateChanged`, que continua
  declarado e nunca emitido.
- Primeira conversa criada automaticamente ou por comando idempotente explícito.
