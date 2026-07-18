# Evidência F1 — recuperação abrupta GNG-2

- Executado em: 2026-07-18T14:48:40Z
- Incremento: EP-05d
- Resultado do critério técnico: verde

Dois testes executaram o `IDurableExecutionEngine` de produção, um sobre SQLite e outro sobre PostgreSQL gerenciado. Em cada provider, um subprocesso adquiriu a execução, persistiu os checkpoints 1–3 e foi encerrado pelo processo de teste com `SIGKILL` (`/bin/kill -9`). Nenhum dispose ou caminho de shutdown do worker participou da recuperação.

Após reinício da autoridade de persistência, o teste comprovou estado `Running`, attempt 1 e checkpoint 3. O reconciliador detectou heartbeat/lease vencido, marcou o primeiro attempt como `abandoned`, incrementou o fencing e programou retry. O replay exato do checkpoint 3 retornou `IdempotentReplay`; o segundo owner adquiriu attempt 2, retomou dos passos 4–6 e concluiu a execução.

Evidência idêntica por provider:

- execução final `Completed`, 2 attempts (`abandoned`, `completed`) e fencing crescente;
- 6 checkpoints únicos (`step-1` a `step-6`), sem perda nem duplicação;
- 8 receipts únicos em `durable_command_inbox` (start, 6 checkpoints e completion);
- 6 transições sequenciais e 6 mensagens Outbox correspondentes;
- razões `execution.started`, `attempt.acquired`, `attempt.reconciled`, `retry.timerFired`, `attempt.acquired`, `attempt.completed`;
- ledger global com 7 elos (provisionamento + 6 transições), sequências contíguas, `previous_hash` íntegro e cada SHA-256 recomputado a partir do dado persistido;
- PostgreSQL criado com prefixo/labels Harness, porta dinâmica, senha em arquivos modo `0600`; cleanup deixou zero containers, volumes, networks ou imagens gerenciadas.

Para tornar a cadeia verificável nos dois providers, `AuditLedgerHash` passou a canonicalizar JSON recursivamente antes do hash, neutralizando espaçamento e reordenação de propriedades por `jsonb`. Um teste unitário prova equivalência entre representações.

Gate executado:

- SQLite recovery focado: 1/1 verde;
- PostgreSQL recovery focado: 1/1 verde;
- `tools/backend/verify.sh`: exit code 0;
- build Release: 0 warnings, 0 errors;
- suíte completa: 65/65 verdes (`Unit 38`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 11`).

O critério técnico de recuperação e auditoria do GNG-2 está comprovado. A Fase 1 não é declarada concluída nesta evidência: EP-06/09/10, documentos e workers ainda precisam satisfazer o escopo definido pela missão.
