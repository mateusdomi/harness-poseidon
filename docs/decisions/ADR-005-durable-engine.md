# ADR-005 — Motor durável específico atrás de interface

- Status: aceito
- Data: 2026-07-18

## Decisão

`IDurableExecutionEngine` cobre ciclo de vida, eventos, timers, retry, leases, fencing, heartbeats, checkpoints, dead-letter e reconciliação. A implementação é específica ao produto e persistida no banco.

## Consequências

Não se cria um framework genérico concorrente. Temporal pode ser backend enterprise futuro da mesma interface, após medição e ADR.
