# ADR-005 — Motor durável específico atrás de interface

- Status: aceito
- Data: 2026-07-18

## Decisão

`IDurableExecutionEngine` cobre ciclo de vida, eventos, timers, retry, leases, fencing, heartbeats, checkpoints, dead-letter e reconciliação. A implementação é específica ao produto e persistida no banco.

## Consequências

Não se cria um framework genérico concorrente. Temporal pode ser backend enterprise futuro da mesma interface, após medição e ADR.

O contrato F1 explicita estados `Ready`, `Running`, `Paused`, `WaitingForRetry`, `WaitingForSignal`, `Completed`, `Cancelled` e `DeadLetter`, com terminais sem transição de saída. Operações esperadas retornam status tipado em vez de exceção: start/pause/resume/cancel, aquisição/renovação/heartbeat com fencing, checkpoint, conclusão/falha, sinal, timer, reconciliação e consulta. Retry usa política decimal determinística, limitada por `MaximumDelay`; a implementação nunca pede ao LLM para decidir backoff ou validade de transição.

O schema F1 materializa estado atual separado de attempts, checkpoints, timers, signals, histórico de transições, dead-letter, Inbox de comandos e Outbox de transições. Índices provider-specific selecionam execuções prontas, leases expiradas, timers vencidos e Outbox pendente. `active_attempt_id` permanece uma invariante transacional da aplicação, sem FK circular, enquanto cada attempt possui FK para execution/tenant e unicidade de número e fencing token.
