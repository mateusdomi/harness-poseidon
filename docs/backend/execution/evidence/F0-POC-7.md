# Evidência F0 PoC-7 — SignalR sequenciado e re-sync snapshot+delta

- Executado em: 2026-07-18T12:56:37Z
- Ambiente: macOS arm64, .NET SDK 10.0.302, ASP.NET Core/SignalR 10.0.10
- Resultado: verde

## Cenário executado

O teste iniciou o `Harness.Host` real em porta loopback dinâmica e conectou um cliente SignalR a `/hubs/events`:

1. O cliente assinou `project:01ARZ3NDEKTSV4RRFFQ69G5FAV` pelo método `Subscribe`.
2. Publicações `task.stateChanged` e `attempt.started` chegaram como envelopes de sequência 1 e 2.
3. A conexão foi encerrada deliberadamente.
4. Durante a desconexão, `attempt.heartbeat`, `gate.changed` e `progress.updated` foram anexados nas sequências 3, 4 e 5; nenhum chegou ao cliente desconectado.
5. `GET /api/v1/event-streams/snapshot?...&afterSequence=2` retornou `sequence=5`, o snapshot `latestByType` e delta ordenado exatamente `[3,4,5]`.
6. Cursor negativo retornou HTTP 400 com `Content-Type: application/problem+json`.
7. Uma nova conexão assinou a mesma stream; o próximo evento chegou na sequência 6. O histórico live observado ficou `[1,2,6]`, provando que o delta não foi duplicado pelo hub.

O Host usa um grupo SignalR por stream e o método cliente único `event`. Payload é obrigatoriamente objeto JSON, timestamp é UTC e tipo precisa pertencer ao catálogo canônico de 28 eventos.

## Contratos e segurança de dependência

- `docs/contracts/events.json` contém hub, envelope, endpoint de re-sync e os 28 nomes tipados.
- `docs/contracts/openapi.json` é gerado pelo Host; título, tags e ausência de server dinâmico tornam o artefato determinístico.
- Contract tests comparam `events.json` a `EventTypeCatalog` e o OpenAPI publicado ao documento do Host em execução.
- O primeiro restore de `Microsoft.AspNetCore.OpenApi 10.0.10` foi bloqueado por `NU1903`: dependência transitiva `Microsoft.OpenApi 2.0.0`, afetada por GHSA-v5pm-xwqc-g5wc/CVE-2026-49451. O advisory indica `2.7.5` como primeira versão 2.x corrigida; ela foi pinada centralmente e o audit passou sem supressão.

O primeiro ensaio SignalR falhou com erro genérico ao invocar `Subscribe`. Detailed errors foram habilitados somente no ambiente Development do teste; a reprodução revelou que o `CancellationToken` era interpretado como segundo argumento remoto. O Hub passou a usar `Context.ConnectionAborted`, mantendo um único argumento público, e as execuções seguintes ficaram verdes.

## Comandos e saídas

```bash
tools/backend/export-contracts.sh
tools/backend/dotnet.sh test tests/Harness.ContractTests/Harness.ContractTests.csproj -c Release --no-build --filter 'FullyQualifiedName~EventCatalogContractTests|FullyQualifiedName~OpenApiContractTests'
tools/backend/dotnet.sh test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj -c Release --no-build --filter FullyQualifiedName~SignalRResynchronizationPocTests
```

Exit codes: 0. Build: 0 warnings/0 errors. Contratos: 2/2 testes verdes. SignalR/re-sync: 1/1 verde em seis execuções consecutivas (205–218 ms). O gate final `tools/backend/verify.sh` passou restore locked, format, build e 46/46 testes. Todos os Hosts de teste encerraram de forma limpa, nenhuma porta ficou órfã e os filtros Docker gerenciados ficaram vazios.

Esta PoC valida o contrato e a recuperação de lacuna. Persistência/Outbox e retenção paginada entram na Fase 1.
