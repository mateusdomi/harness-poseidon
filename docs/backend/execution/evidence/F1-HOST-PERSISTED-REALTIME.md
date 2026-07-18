# F1-WRK-1d.3b — Host pessoal com realtime persistido

Data UTC: 2026-07-18

## Escopo executado

- O modo pessoal do Host passou a possuir um único `SqliteWriteDispatcher` compartilhado por Runner IPC, Outbox e realtime.
- `SqliteMigrationHostedService` aplica migrations idempotentes antes do worker da Outbox.
- `OutboxDispatcherBackgroundService` usa `SqliteOutboxStore`, `PersistedRealtimeOutboxSink` e `SignalRRealtimeEventBroadcaster` registrados no DI do Host.
- O snapshot HTTP e `EventsHub.GetStreamSnapshot` leem `IRealtimeEventStore` assincronamente.
- O `EventStreamStore` em memória foi removido; o caminho de produção tem uma única fonte de verdade persistida.
- O store do Runner aceita o dispatcher compartilhado sem assumir sua propriedade ou descartá-lo.

## Prova end-to-end

`SignalRResynchronizationPocTests.PersistedOutboxResynchronizesHttpAndSignalRAfterHostRestart` executou com banco SQLite temporário real e comprovou:

1. evento 1 entregue ao vivo pelo fluxo Outbox → store persistido → SignalR;
2. desconexão do cliente e persistência dos eventos 2–3;
3. snapshot HTTP após cursor 1 retornando delta `[2,3]`;
4. shutdown limpo do Host e restart com o mesmo banco;
5. replay deliberado da mensagem 1 da Outbox após restart sem nova transmissão e sem novo sequence;
6. snapshot pelo método SignalR retornando o mesmo delta `[2,3]`;
7. reconexão recebendo somente o evento novo 4;
8. snapshot final contíguo `[1,2,3,4]`, sem lacuna, duplicação ou reutilização;
9. quatro mensagens da Outbox em estado dispatched;
10. cleanup do banco/artefatos e shutdown cancelável.

O teste do IPC também passou a iniciar o Host sobre o mesmo ciclo de vida compartilhado e comprovou replay 3/3 após restart sem conexão direta do Runner ao banco.

## Comandos e resultados

```text
tools/backend/dotnet.sh build Harness.sln --configuration Release --no-restore
exit code: 0
0 warnings, 0 errors

tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --configuration Release --no-build --no-restore \
  --filter 'FullyQualifiedName~SignalRResynchronizationPocTests|FullyQualifiedName~RunnerHostIpcPocTests'
exit code: 0
2/2 passed

tools/backend/dotnet.sh test tests/Harness.ContractTests/Harness.ContractTests.csproj \
  --configuration Release --no-build --no-restore --filter FullyQualifiedName~OpenApiContractTests
exit code: 0
1/1 passed

tools/backend/verify.sh
exit code: 0
restore locked, format check, Release build 0 warnings/0 errors, 104/104 tests passed
```

Ao final: nenhum processo Host/Runner/Launcher e nenhum container, volume ou network com `com.harness.managed=true`.
