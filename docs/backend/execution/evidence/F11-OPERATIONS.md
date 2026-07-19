# Evidência F11-6 — instalação, operação e resposta a incidentes

Data: 2026-07-19.

## Entregas

- `tools/backend/publish-server.sh`: pacote Host self-contained por RID com frontend embarcado.
- `docs/backend/operations/INSTALLATION.md`: modos pessoal/servidor, configuração PostgreSQL/OIDC,
  borda TLS/CORS e gate de instalação.
- `docs/backend/operations/OPERATIONS.md`: start/health/shutdown, backup/restore, atualização,
  segredo e gates operacionais.
- `docs/backend/operations/INCIDENT_RUNBOOK.md`: contenção, segredo exposto, banco/upgrade,
  runner/lease/worktree/Docker e resync realtime.
- `tools/backend/verify-operations.sh`: valida sintaxe/permissões, build Release, Launcher, Host
  PostgreSQL, backup/restore e higiene de segredos.

## Defeito encontrado pelo próprio gate

O primeiro publish servidor revelou que `dotnet publish --runtime osx-arm64` reavaliava os
`packages.lock.json` rastreados com um target RID. O restore locked canônico seguinte falhava com
NU1004. Os publicadores desktop e servidor agora registram trap de saída que restaura o grafo
canônico com `restore --force-evaluate`, inclusive após falha do publish. A repetição comprovou:

1. publish servidor self-contained concluiu;
2. nenhum lockfile ficou alterado;
3. `restore Harness.sln --locked-mode` voltou a exit 0;
4. o gate operacional passou.

## Execução

- Frontend durante publish: 331/331 testes e build de produção verde.
- Publish `osx-arm64`: Host self-contained + `wwwroot` concluído.
- `verify-operations.sh`: build Release 0 warnings/0 erros; **3/3** integração verdes
  (`LauncherSmokeTests`, `PostgresServerModeHostTests`, `LocalOperationsApiTests`).
- `scan-secrets.sh`: limpo.
- Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi alterado.
