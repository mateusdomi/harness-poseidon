# Evidência F1 — metadados e lifecycle documental

- Executado em: 2026-07-18T16:58:12Z
- Incremento: F1-DOC-1c.3
- Resultado: verde

`UpdateMetadataAsync` valida classificações únicas/ordenadas, fase opcional, flag de inconsistência, versão esperada e idempotency key. A aplicação substitui classificações e atualiza fase/flag/version/updatedAt na mesma transação da Inbox, ledger e Outbox. Não grava `document_state_transitions`, pois o estado não mudou.

`TransitionAsync` valida ULIDs, catálogo de estados/atores, vínculo obrigatório de actor id para user/chief/agent, nota normalizada e matriz fechada do agregado. Uma aplicação atualiza estado/versão, anexa uma transição imutável com from/to/ator/nota/versão e finaliza Inbox, ledger e Outbox `document.stateChanged` atomicamente. SQLite usa o dispatcher; PostgreSQL combina advisory lock idempotente com `FOR UPDATE` no documento e lock do ledger por tenant.

O cenário dual-provider criou o documento como órfão (`phaseName=null`), anexou v2, e dez atualizações concorrentes o adotaram na fase `Review` com novas classificações/inconsistência: uma aplicação e nove replays, zero transições de estado. Em seguida, dez transições `in_elaboration→in_review` produziram uma aplicação/nove replays e exatamente uma row histórica na versão agregada 4. Um salto proibido `in_review→approved` retornou `InvalidState`, foi reexecutado pela Inbox e permaneceu sem ledger/Outbox ou segunda transição.

Execuções focadas SQLite 1/1 e PostgreSQL 1/1 passaram após build Release 0 warnings/0 errors. Inventário Docker preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos `com.harness.managed=true`. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build 0 warnings/0 errors e 92/92 testes (`Unit 63`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

O próximo incremento persiste solicitação, resolução e cancelamento de aprovação vinculados à versão corrente, com uma única pendência e eventos `approval.requested`/`approval.resolved`.
