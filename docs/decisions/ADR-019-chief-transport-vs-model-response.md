# ADR-019 — Separar confirmação de transporte da resposta real do Chief

- Status: aceito
- Data: 2026-07-21

## Contexto

O pipeline durável do Chief (mailbox `chief_turn_mailbox`, lease/fencing,
receipt de governança, evaluator independente) é real e recuperável, mas o turno
sempre executa o `FakeAgentExecutor`, ligado incondicionalmente no compositor
(`HostApplication`). O texto fixo do Fake — "O turno foi registrado de forma
durável para: {assunto}" — é apresentado ao usuário como se fosse a resposta do
modelo. Além disso, o endpoint de turno responde `202 Accepted` com um handle,
o que é um **acknowledgement de transporte**, não uma resposta do Chief; a UI
não consegue distinguir as duas coisas. O executor real (`CodexCliAgentExecutor`)
só é alcançável pelo caminho de tentativa isolada, nunca pelo chat.

## Decisão

### 1. Ciclo de vida tipado do turno

O domínio e os eventos distinguem explicitamente as fases do turno, cada uma
com evento próprio no stream da conversa/projeto:

```
message.received     — mensagem humana persistida
turn.registered      — turno enfileirado de forma durável (ACK de transporte)
execution.enqueued   — item de execução criado para o worker
provider.invoked     — provider real efetivamente chamado
model.responded       — resposta do modelo recebida e validada pelo schema
demand.created       — demanda derivada da resposta
execution.blocked    — execução bloqueada por prontidão (nenhuma resposta produzida)
```

Os eventos `chat.turnStarted/turnChunk/turnCompleted` e `chief.turnStateChanged`
(este último já declarado no catálogo, porém nunca emitido) permanecem e passam a
ser efetivamente emitidos nas transições correspondentes. `turn.registered`
**nunca** é apresentado como resposta do modelo.

### 2. Fail-closed quando não há Chief real

Se provider/model/workflow não resolverem (via read model de prontidão,
ADR-017), o endpoint de turno **não finge resposta**: retorna um estado
bloqueado tipado (`execution.blocked`) com `blockers[]` e `nextActions[]`, sem
enfileirar turno de modelo. Nenhum `FakeAgentExecutor` participa do pacote de
homologação normal.

### 3. Executor simulado é opt-in e rotulado

O `FakeAgentExecutor` só é ligado sob modo simulado explícito (`Harness:Demo:Enabled`
ou configuração de execução isolada `Fake`), e suas saídas carregam
`executionMode=simulated`. A automação determinística de teste continua usando o
Fake, sempre rotulada. O smoke com turno real permanece opt-in
(`HARNESS_RUN_REAL_AGENT_TESTS=true`, ADR-008) e adiciona um gate condicional que
exige, com credencial externa: provider real → modelo real → uma mensagem →
resposta não simulada → demanda → tarefas → evidência → critic → gate.

### 4. Primeira conversa

A primeira conversa pode ser criada automaticamente ou por comando idempotente
explícito, para que o usuário não precise descobrir "Nova conversa" para
desbloquear o input. Uma conversa sem prontidão exibe blockers tipados em vez de
aceitar mensagem silenciosamente.

## Consequências

- A UI passa a distinguir "registrado" de "respondido" e "bloqueado", eliminando
  a percepção de resposta real onde há apenas confirmação de transporte.
- O gate do golden path pode asserir que nenhuma resposta fake é apresentada como
  real e que o bloqueio é explícito.
- Custo: novos tipos de evento (publicados primeiro, ADR-017/handoff), emissão
  nas transições e ligação condicional do executor. A ligação incondicional do
  Fake no compositor é substituída por seleção guiada por modo.
