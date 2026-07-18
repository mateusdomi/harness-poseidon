# Evidência F0 PoC-9 — IPC Host–Runner autenticado e idempotente

- Executado em: 2026-07-18T13:29:41Z
- Ambiente: macOS arm64, .NET SDK 10.0.302/runtime 10.0.10, Kestrel HTTP loopback dinâmico
- Resultado: verde; completa GNG-1 em 9/9

## Cenário executado

O teste iniciou `Harness.Host` em `127.0.0.1:0`, criou token aleatório de 256 bits em arquivo efêmero `0600` e executou quatro subprocessos reais de `Harness.Runner.dll`:

1. Runner autorizado enviou heartbeat sequência 1, checkpoint sequência 2 e completion sequência 3. O Host registrou exatamente um heartbeat, `checkpoint-2`, conclusão e três entradas de Inbox.
2. O mesmo Runner repetiu as três mensagens com as mesmas idempotency keys. Recebeu três receipts `replay=true`; `lastSequence`, contadores, checkpoint, conclusão e `InboxCount=3` permaneceram inalterados.
3. Uma tentativa nova começou na sequência 2. O Host retornou HTTP 409/`application/problem+json` com `runner_sequence_gap`, informou a sequência esperada e não criou estado.
4. Outro Runner usou token incorreto. O Host retornou HTTP 401/`runner_ipc_unauthorized` e não criou estado.

O Runner valida HTTP loopback antes do envio; o Host também valida `RemoteIpAddress`. O bearer token não aparece em argumentos do processo, ambiente, stdout ou stderr. A comparação do token usa hashes SHA-256 e `CryptographicOperations.FixedTimeEquals`. Todos os processos encerraram; o diretório com tokens foi removido no `finally`.

## Autoridade e arquitetura

O Runner contém somente contratos SharedKernel e cliente HTTP. Um teste carrega o assembly final e comprova ausência de referências a Persistence, EF Core, Npgsql e SQLite. Migrations e banco continuam exclusivos do Host. O store in-memory da PoC isola o protocolo; a primeira fatia da Fase 1 o move para Inbox/estado relacional sob application service.

O endpoint interno foi marcado `ExcludeFromDescription`, portanto `docs/contracts/openapi.json` permaneceu canônico e o drift test passou sem alteração. Erros IPC seguem RFC 7807.

## Comandos e saídas

```bash
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~RunnerHostIpcPocTests
tools/backend/dotnet.sh test tests/Harness.ArchitectureTests/Harness.ArchitectureTests.csproj -c Release --no-build --filter FullyQualifiedName~RunnerHasNoPersistenceOrDatabaseDependency
tools/backend/verify.sh
```

A versão final passou seis execuções consecutivas (374–404 ms), cada uma com quatro subprocessos. O gate completo passou restore locked, format, build Release com 0 warnings/0 errors e 49/49 testes: unit 28, architecture 6, concurrency 3, recovery 2, contract 3 e integration 7. Nenhum Host/Runner/Launcher ficou ativo e nenhum arquivo da Kimi foi alterado.

Uma execução intermediária de `dotnet restore --force-evaluate` terminou com exit 139 sem saída diagnóstica. A repetição isolada com verbosity normal passou em 0,91 s, e restores locked/builds posteriores ficaram verdes; o risco está monitorado em `RISKS.md`.
