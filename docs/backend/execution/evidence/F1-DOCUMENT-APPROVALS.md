# Evidência F1 — aprovações documentais transacionais

- Executado em: 2026-07-18T17:07:39Z
- Incremento: F1-DOC-1c.4
- Resultado: verde

`RequestApprovalAsync`, `ResolveApprovalAsync` e `CancelApprovalAsync` validam ULIDs, versão esperada, prazos, catálogo de prioridade/decisão/ator, textos e idempotency key. Request vincula-se ao `document_version_id` corrente e a unicidade parcial garante uma única pendência. Rejeição sem nota retorna `RejectionNoteRequired` como receipt idempotente; não lança, não altera estado e não cria ledger/Outbox.

Cada aplicação atualiza atomicamente o request, o estado/version do documento, a transição append-only, Inbox, ledger encadeado e Outbox. Eventos primários são `approval.requested` e `approval.resolved`. SQLite usa o dispatcher único; PostgreSQL usa advisory lock por idempotency key, `FOR UPDATE` no documento e lock do ledger por tenant.

O cenário comum partiu de `in_review` v4. Dez requests concorrentes produziram uma aplicação/nove replays e estado `awaiting_approval` v5. Segundo request recebeu `ApprovalAlreadyPending` sem auditoria. Cancelamento produziu v6/`in_review`; novo request v7; rejeição sem nota foi recusada e reexecutada; rejeição com nota produziu v8/`in_elaboration`. A correção criou conteúdo v3/agregado v9, voltou à review v10, pediu aprovação v11 e dez decisões finais produziram uma aplicação/nove replays em v12/`approved`.

O snapshot integral comprovou três versões de conteúdo, três requests históricos ordenados (`cancelled`, `rejected`, `approved`), cada resolução em request version 2, e oito transições de estado ordenadas. O request final aponta para a versão de conteúdo v3; requests anteriores preservam seus vínculos imutáveis.

Execuções focadas SQLite 1/1 e PostgreSQL 1/1 passaram após build Release 0 warnings/0 errors. Inventário preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos `com.harness.managed=true`. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build 0 warnings/0 errors e 92/92 testes (`Unit 63`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

F1-DOC-1c está completo. O próximo incremento inicia os workers persistidos pela Outbox, seguido de watchdog/reconciliação e publicação SignalR.
