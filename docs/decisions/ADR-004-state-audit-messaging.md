# ADR-004 — Estado relacional, ledger, Inbox e Outbox

- Status: aceito
- Data: 2026-07-18

## Decisão

Estado atual reside em tabelas relacionais com histórico explícito. Auditoria usa ledger append-only com hash encadeado por tenant. Integrações duráveis usam Outbox; recepção idempotente usa Inbox; falhas terminais usam dead-letter.

## Consequências

Não haverá event sourcing completo. Toda transição passa por application service e transação autorizada; agentes nunca escrevem tabelas diretamente.
