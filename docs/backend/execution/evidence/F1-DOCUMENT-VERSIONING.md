# Evidência F1 — versionamento transacional de documentos

- Executado em: 2026-07-18T16:51:27Z
- Incremento: F1-DOC-1c.2
- Resultado: verde

`AppendVersionAsync` exige ULIDs canônicos, path relativo sem traversal, SHA-256, autor, idempotency key e `expectedDocumentVersion` positivo. A mutação só é válida em `in_elaboration`.

O SQLite executa no dispatcher único. O PostgreSQL serializa a chave idempotente e bloqueia a row do documento com `FOR UPDATE`. Uma aplicação insere exclusivamente uma nova `document_versions`, liga `supersedes_id` à versão corrente imutável, avança `documents.current_version` e a versão otimista, e grava ledger/Outbox `document.stateChanged` e Inbox na mesma transação.

Dez chamadas simultâneas da mesma correção produziram exatamente uma aplicação e nove replays com receipt idêntico: agregado v2, conteúdo v2 e mesma sequência/hash/outbox. O snapshot recompôs v1 e v2 em ordem, com v2→v1. Uma nova chave com versão esperada stale retornou `VersionConflict`; documento ausente retornou `NotFound`. Ambas as rejeições foram persistidas na Inbox, repetidas deterministicamente e permaneceram sem ledger/Outbox. Reuso da chave stale com payload diferente lançou `IdempotencyConflictException`.

Execuções focadas SQLite 1/1 e PostgreSQL 1/1 passaram após build Release 0 warnings/0 errors. Inventário Docker confirmou 11 containers de terceiros parados, 14 volumes, 7 networks e portas em uso; cleanup final confirmou zero recursos `com.harness.managed=true`. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build 0 warnings/0 errors e 92/92 testes (`Unit 63`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

O próximo incremento implementa classificação/fase e lifecycle, incluindo adoção de órfãos e histórico de transições append-only.
