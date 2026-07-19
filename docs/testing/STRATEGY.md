# Estratégia de testes

## Suítes

- `Harness.UnitTests`: regras puras, IDs, resultados, políticas e máquinas de estado.
- `Harness.IntegrationTests`: API/application services e persistência SQLite/PostgreSQL.
- `Harness.ArchitectureTests`: topologia e dependências com XML + ArchUnitNET.
- `Harness.ContractTests`: OpenAPI, eventos, RFC 7807, serialização e drift frontend.
- `Harness.RecoveryTests`: encerramento abrupto, checkpoint e reconciliação.
- `Harness.ConcurrencyTests`: dispatcher, leases, fencing, idempotência e aquisição concorrente.

## Pipeline local

`tools/backend/verify.sh` executa restore em locked mode, `dotnet format --verify-no-changes`, build Release com warnings como erros e as seis suítes. Testes reais de agente são opt-in por `HARNESS_RUN_REAL_AGENT_TESTS=true`.

`tools/backend/verify-resilience.sh` é o gate focado de release para upgrade, backup/restore,
migração SQLite→PostgreSQL, carga/isolamento de 30 usuários e recuperação abrupta dual-provider.
Ele também falha quando um container, volume ou network Docker gerenciado fica órfão.

## Evidência

Cada incremento registra comando, data, exit code e contagem em `docs/backend/execution/PROGRESS.md`. Arquivo criado sem execução não conta como concluído. Falhas repetidas exigem reproduzível/instrumentação antes de nova edição.
