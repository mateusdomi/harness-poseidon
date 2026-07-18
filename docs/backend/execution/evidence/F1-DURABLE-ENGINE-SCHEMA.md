# Evidência F1 — schema do motor durável

- Executado em: 2026-07-18T14:10:01Z
- Incremento: EP-05b
- Resultado: verde

As migrations `0003_durable_execution.sql` (SQLite) e `0004_durable_execution.sql` (PostgreSQL) materializam nove estruturas conceituais equivalentes: executions, attempts, checkpoints, timers, signals, transition history, dead-letter, command Inbox e execution Outbox. Execution pertence obrigatoriamente a tenant/projeto; attempt pertence a execution/tenant e mantém owner, lease, heartbeat e fencing token crescente. Estados, contadores, versões e limites de retry possuem constraints.

Os índices parciais são específicos de cada provider para aquisição de `ready`, reconciliação de attempts expiradas, timers vencidos e despacho Outbox. SQLite armazena timestamps ISO-8601 e multiplicador decimal textual validado; PostgreSQL usa `timestamptz`, `jsonb` e `numeric(18,6)`. Não há SQL compartilhado entre providers.

O teste SQLite comprovou migration `3→0`, presença das nove tabelas, FK com fundação e rejeição de estado desconhecido. O teste PostgreSQL comprovou migration `4→0`, as mesmas nove tabelas, inserção ligada ao tenant/projeto e SQLSTATE de check violation para estado desconhecido.

Gate: testes focados 2/2; `tools/backend/verify.sh` exit 0; format verde; build Release 0 warnings/0 errors; 58/58 testes verdes. O container PostgreSQL e todos os recursos com label Harness foram removidos; áreas da Kimi permaneceram intactas.
