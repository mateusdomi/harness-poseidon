# ADR-004 — Estado relacional, ledger, Inbox e Outbox

- Status: aceito
- Data: 2026-07-18

## Decisão

Estado atual reside em tabelas relacionais com histórico explícito. Auditoria usa ledger append-only com hash encadeado por tenant. Integrações duráveis usam Outbox; recepção idempotente usa Inbox; falhas terminais usam dead-letter.

## Consequências

Não haverá event sourcing completo. Toda transição passa por application service e transação autorizada; agentes nunca escrevem tabelas diretamente.

O primeiro incremento F1 materializa `IFoundationTransactionStore` com implementações provider-specific. No SQLite, toda transação entra pelo dispatcher de single writer. No PostgreSQL, um advisory lock derivado de `(tenant, idempotencyKey)` serializa a Inbox antes da mutação. A receipt é persistida na Inbox; replay com o mesmo hash retorna a receipt sem novo efeito e chave reutilizada com hash diferente falha. Estado, elo do ledger SHA-256, Outbox e Inbox commitam juntos; violação em qualquer insert faz rollback integral.
