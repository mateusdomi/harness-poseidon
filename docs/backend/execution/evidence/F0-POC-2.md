# Evidência F0 PoC-2 — kill -9 e retomada durável

- Executado em: 2026-07-18T12:04:03Z
- Ambiente: macOS arm64, .NET SDK 10.0.302, SQLite WAL
- Resultado: verde

## Cenário executado

1. A instância inicial criou uma tarefa sintética de 6 passos no SQLite.
2. Um processo .NET separado adquiriu a tarefa como `owner-before-kill`.
3. Cada passo persistiu checkpoint e contador na mesma transação.
4. Após o terceiro checkpoint, o processo escreveu sinal de prontidão e ficou bloqueado.
5. O teste executou `/bin/kill -9 <pid>` no PID exato e confirmou exit code não zero.
6. Uma nova instância abriu o mesmo banco e observou estado `running`, 3 passos, 3 checkpoints e 1 tentativa.
7. O reconciliador moveu exatamente uma tarefa interrompida para `pending` e invalidou o owner.
8. `owner-after-restart` retomou do checkpoint 3 e concluiu.

Estado final comprovado: `completed`, 6/6 passos, 6 checkpoints únicos, 2 tentativas, 1 reconciliação e nenhum owner ativo. Portanto, não houve perda nem duplicação.

## Comandos e saídas

```bash
tools/backend/dotnet.sh build tests/Harness.RecoveryTests/Harness.RecoveryTests.csproj --configuration Release --no-restore
tools/backend/dotnet.sh test tests/Harness.RecoveryTests/Harness.RecoveryTests.csproj --configuration Release --no-build --no-restore --filter FullyQualifiedName~DurableExecutionRecoveryPocTests --logger 'console;verbosity=normal'
```

Exit codes: 0. Build: 0 warnings/0 errors. Execução inicial: 1/1 teste aprovado (`SigkillIsReconciledWithoutLostOrDuplicateCheckpoints`) em 523 ms.

O cenário completo com processo + SIGKILL foi repetido cinco vezes adicionais, sempre 1/1 aprovado (103–115 ms registrados pelo runner). Depois, `tools/backend/verify.sh` terminou com exit code 0 e 34 testes verdes nas seis suítes.

## Cleanup

Após as repetições, a inspeção de processos encontrou zero `Harness.RecoveryTests.dll durable-worker`. A inspeção dos artefatos encontrou zero arquivos `.db`, `.db-wal` ou `.db-shm` restantes.

Esta PoC comprova a estratégia de persistência/reconciliação; não declara o `IDurableExecutionEngine` completo da Fase 1 concluído.
