# ADR-004 — Estado relacional, ledger, Inbox e Outbox

- Status: aceito
- Data: 2026-07-18

## Decisão

Estado atual reside em tabelas relacionais com histórico explícito. Auditoria usa ledger append-only com hash encadeado por tenant. Integrações duráveis usam Outbox; recepção idempotente usa Inbox; falhas terminais usam dead-letter.

## Consequências

Não haverá event sourcing completo. Toda transição passa por application service e transação autorizada; agentes nunca escrevem tabelas diretamente.

O primeiro incremento F1 materializa `IFoundationTransactionStore` com implementações provider-specific. No SQLite, toda transação entra pelo dispatcher de single writer. No PostgreSQL, um advisory lock derivado de `(tenant, idempotencyKey)` serializa a Inbox antes da mutação. A receipt é persistida na Inbox; replay com o mesmo hash retorna a receipt sem novo efeito e chave reutilizada com hash diferente falha. Estado, elo do ledger SHA-256, Outbox e Inbox commitam juntos; violação em qualquer insert faz rollback integral.

O IPC Runner–Host segue a mesma autoridade por `IRunnerMessageStore`: a política de transição é compartilhada e os SQLs/migrations são provider-specific. SQLite reutiliza o dispatcher; PostgreSQL adquire advisory lock transacional por `attemptId`. Tentativa, sequência, checkpoint, Inbox e Outbox commitam atomicamente. O Host aplica migrations SQLite de modo lazy e idempotente; o Runner conhece apenas o envelope HTTP e nunca referencia os assemblies de persistência.

As tabelas `runner_*` são uma projeção de transporte e idempotência do IPC, não o agregado de domínio Tentativa. O EP-05 conectará essa projeção a uma tentativa previamente autorizada pelo motor durável e ao tenant correspondente; receber sequência 1 do Runner não concede autoridade para criar demanda, tarefa ou tentativa de domínio.

Cada transição do motor durável também anexa `audit_ledger` na mesma transação que estado, histórico e Outbox. SQLite herda a serialização do dispatcher; PostgreSQL adquire um advisory lock adicional por tenant para calcular sequência e hash sem bifurcar a cadeia quando execuções diferentes avançam em paralelo. O payload é canonicalizado recursivamente, ordenando propriedades JSON, antes do SHA-256; assim o elo continua verificável depois que `jsonb` normaliza espaçamento ou ordem de propriedades.
