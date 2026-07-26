# Runbook de recuperação

## Detecção

O watchdog identifica leases expiradas, heartbeats ausentes, tentativas órfãs,
execuções em retry, outbox pendente e itens em dead letter. Antes de agir, confirme
provider de banco, tenant, projeto, execução e fencing token sem expor payloads.

## Recuperação

1. Reidratar estado de `durable_executions` e `durable_attempts`.
2. Invalidar o owner e fencing token expirados.
3. Verificar idempotency key, último checkpoint e efeitos confirmados.
4. Restaurar o workspace pelo `GitCheckpointContext`.
5. Reenfileirar com novo fencing token e contexto referenciado pelo mesmo snapshot,
   ou mover para dead letter quando a política de retry acabar.
6. Reprocessar outbox de forma idempotente.
7. Reconciliar projeções com ledger e fonte factual.

Resultado tardio da tentativa antiga é descartado. Nunca edite o ledger para
forçar continuidade.

## Escalonamento

Falha repetida, corrupção, ausência de checkpoint, divergência dual ou efeito
externo não reconciliável bloqueia a execução e abre incidente. Recuperação de
banco usa backup SQLite no modo pessoal ou PITR no servidor, com RPO e RTO definidos
por ambiente.
