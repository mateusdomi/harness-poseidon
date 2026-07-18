# Evidência F1 — contrato e schema de dispatch da Outbox

- Executado em: 2026-07-18T17:13:04Z
- Incremento: F1-WRK-1a
- Resultado: verde

`IOutboxStore` define aquisição da próxima mensagem, lease com fencing token, completion, falha com política de retry, liberação de claims expirados e snapshot operacional. O contrato valida owner, lease, ULIDs, payload, erro e timestamps. `OutboxRetryPolicy` calcula backoff exponencial em decimal, determinístico e limitado por máximo configurado; 2/2 testes comprovaram sequência 1/2/4/5/5 segundos e rejeição de política/attempt inválidos.

As migrations SQLite `0007_outbox_dispatch` e PostgreSQL `0008_outbox_dispatch` acrescentam `available_at`, owner/token/expiração do claim, último erro e dead-letter à tabela existente. Mensagens antigas e novos inserts que omitem `available_at` continuam elegíveis por `COALESCE(available_at,occurred_at)`. `outbox_dispatch_failures` registra cada falha com attempt e indicador terminal; triggers impedem update/delete do histórico.

Índices parciais cobrem mensagens dispatchable e claims expirados. PostgreSQL também fecha consistência owner/expiry e exclusão mútua dispatched/dead-letter com constraints. SQLite mantém os mesmos invariantes comportamentais no store/dispatcher único, compatível com limitações de `ALTER TABLE`.

Execuções: policy 2/2; SQLite schema/migrations 3/3 com `7→0`; PostgreSQL 1/1 com `8→0`. Inventário Docker preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos `com.harness.managed=true`. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build 0 warnings/0 errors e 94/94 testes (`Unit 65`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

O próximo incremento implementa os stores: SQLite serializado no dispatcher e PostgreSQL com `FOR UPDATE SKIP LOCKED`, ambos com fencing, retry/dead-letter e recuperação de claim expirado.
