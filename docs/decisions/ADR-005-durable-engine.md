# ADR-005 — Motor durável específico atrás de interface

- Status: aceito
- Data: 2026-07-18

## Decisão

`IDurableExecutionEngine` cobre ciclo de vida, eventos, timers, retry, leases, fencing, heartbeats, checkpoints, dead-letter e reconciliação. A implementação é específica ao produto e persistida no banco.

## Consequências

Não se cria um framework genérico concorrente. Temporal pode ser backend enterprise futuro da mesma interface, após medição e ADR.

O contrato F1 explicita estados `Ready`, `Running`, `Paused`, `WaitingForRetry`, `WaitingForSignal`, `Completed`, `Cancelled` e `DeadLetter`, com terminais sem transição de saída. Operações esperadas retornam status tipado em vez de exceção: start/pause/resume/cancel, aquisição/renovação/heartbeat com fencing, checkpoint, conclusão/falha, sinal, timer, reconciliação e consulta. Retry usa política decimal determinística, limitada por `MaximumDelay`; a implementação nunca pede ao LLM para decidir backoff ou validade de transição.
