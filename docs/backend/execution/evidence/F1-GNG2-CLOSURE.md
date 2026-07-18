# Encerramento formal da Fase 1 — GNG-2

Data UTC: 2026-07-18

Estado: **verde**.

## Critério de saída

Uma tarefa em andamento sobrevive a encerramento abrupto e é reconciliada sem perda ou duplicação, com auditoria completa, nos dois providers.

## Resultado observado

- `SIGKILL` real após checkpoint 3/6 em SQLite e PostgreSQL.
- Watchdog reiniciado descobre o tenant e reconcilia automaticamente o attempt expirado.
- Attempt 1 termina `abandoned`; attempt 2 usa fencing maior e retoma do checkpoint 3.
- Execução conclui 6/6 checkpoints únicos.
- Estado final possui 2 attempts, 8 receipts de Inbox, 6 transições, 6 eventos de Outbox e 7 entradas de ledger com hash encadeado válido.
- Replay do watchdog, checkpoint, Outbox e realtime é idempotente.
- Timers, signals/timeouts e retries reativam trabalho sem LLM; limite de retry produz dead-letter.
- Runner permanece sem referência ou conexão de escrita com persistência.
- Host pessoal usa um dispatcher SQLite e um ciclo de migrations/stores/workers.
- Migrations SQLite e PostgreSQL são idempotentes e provider-specific.
- `tools/backend/verify.sh`: 104/104, Release 0 warnings/0 errors.
- Zero recurso Docker Harness, processo ou fixture órfã após a suíte.

Evidências detalhadas: `F1-GNG2-RECOVERY.md`, `F1-HOST-PERSISTED-REALTIME.md` e `F1-WATCHDOG-RECONCILIATION.md`.

Com o escopo restante indicado pelo handoff concluído e o critério executado, a Fase 1/GNG-2 está formalmente encerrada. O próximo caminho crítico autorizado é a Fase 2 — MVP pessoal.
