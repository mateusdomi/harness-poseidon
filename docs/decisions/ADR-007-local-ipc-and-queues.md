# ADR-007 — IPC loopback autenticado e filas internas

- Status: aceito
- Data: 2026-07-18

## Decisão

Launcher cria porta dinâmica e token efêmero. Runner comunica heartbeat, checkpoint e resultado ao Host por HTTP somente em loopback, com `runnerId`, `attemptId`, `sequence` e `idempotencyKey`. `System.Threading.Channels` existe apenas dentro do Host.

## Consequências

O token nunca é logado. Mensagem repetida não duplica efeito e sequência fora de ordem é rejeitada ou reconciliada. RabbitMQ fica fora do modo pessoal.
