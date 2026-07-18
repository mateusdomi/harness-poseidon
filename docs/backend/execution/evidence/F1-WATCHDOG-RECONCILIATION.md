# F1-WRK-2 — Watchdog e reconciliador durável

Data UTC: 2026-07-18

## Implementação executada

- `DurableExecutionWatchdogBackgroundService` descobre deterministicamente tenants com execuções em `running`, `waiting_retry` ou `waiting_signal`.
- Cada ciclo dispara timers/sinais vencidos e retries disponíveis, detecta leases ou heartbeats expirados e usa o reconciliador transacional do motor.
- Retry respeita o backoff persistido; o limite de tentativas produz dead-letter.
- A invalidação do attempt incrementa fencing e impede heartbeat/checkpoint do owner antigo.
- O worker não usa LLM, é cancelável e registra somente o tipo de exceção, sem payload, conexão ou segredo.
- SQLite usa o dispatcher único; PostgreSQL preserva locks transacionais. O mesmo cenário provider-neutral executa nos dois.
- O Host pessoal registra o motor e inicia o watchdog somente depois das migrations.

## Evidência de comportamento

O cenário comum `DurableExecutionWatchdogBehavior` comprovou nos dois providers:

1. dez ciclos concorrentes reconciliam exatamente uma vez;
2. timeout de sinal dispara uma única reativação por timer;
3. attempt stale muda para `waiting_retry`, preservando checkpoint;
4. escrita com fencing antigo retorna `LeaseRejected`;
5. novo worker no mesmo estado não duplica transição, Outbox, ledger ou checkpoint (versão permanece idêntica);
6. vencimento do backoff reativa sem LLM;
7. nova tentativa recebe número 2 e fencing maior, reidratada do checkpoint;
8. nova expiração no limite produz um único dead-letter;
9. nova repetição é no-op e o estado/versionamento permanece idêntico;
10. start/stop do `BackgroundService` conclui de forma cancelável.

Os testes de recuperação de produção foram alterados para usar o watchdog após `SIGKILL` real. SQLite e PostgreSQL reiniciaram sobre o mesmo estado persistido, reconciliaram o attempt perdido, recusaram replay de checkpoint, dispararam retry, retomaram de `step-3` e concluíram `step-6` mantendo a auditoria já comprovada: 2 attempts, 6 checkpoints, 8 Inbox, 6 transições/Outbox e 7 elos de ledger.

## Gates executados

```text
SQLite watchdog integration: 1/1 passed
PostgreSQL watchdog integration: 1/1 passed
SQLite SIGKILL recovery: 1/1 passed

tools/backend/verify.sh
exit code: 0
restore locked
format: clean
Release build: 0 warnings, 0 errors
tests: 104/104 passed
```

Migrations permaneceram idempotentes (`SQLite 8→0`, `PostgreSQL 9→0`). Ao final não havia processo Host/Runner/Launcher nem container, volume ou network com `com.harness.managed=true`.
