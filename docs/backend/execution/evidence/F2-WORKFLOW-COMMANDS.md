# Evidência F2-WF-1b — comandos e lifecycle de workflow

Data: 2026-07-18. Commit funcional validado e publicado: `6078ec7` em `develop`.

## Escopo comprovado

- `POST /workflow-templates/{id}/versions` publica vN imutável, numerada pela autoridade persistida, com fases/gates, `phaseConfigs`, modo padrão, transições e changelog.
- `POST /workflows/{id}/operation-mode` troca entre manual, semiautônomo e autônomo, registra novo aceite de risco e anexa auditoria na mesma transação.
- Comandos de run cobrem pausa, retomada e cancelamento; objetivos avançam monotonicamente; gates humanos registram decisor, instante e nota; falha sem nota é recusada; conclusão de fase ativa a seguinte ou conclui o run.
- `workflow.versionPublished` é sequenciado no stream `global`; `gate.changed` é sequenciado no stream `project:<id>` com payload `{ gateId, runId, from, to, decidedByProfileId, note }`; `audit.eventAppended` publica a troca de modo.
- O cenário HTTP publicou v2, percorreu duas fases, falhou e reaprovou gate, concluiu o run e recuperou estado/eventos após restart do Host no mesmo SQLite.

## Persistência e compatibilidade

SQLite ganhou migrations aditivas `0015_workflow_commands` e `0016_global_realtime_stream`. PostgreSQL ganhou `0010_global_realtime_stream`, preservando o histórico e aceitando o mesmo contrato de stream usado pelo teste comportamental dual-provider. Execuções frescas/repetidas resultaram em `16→0` no SQLite e `10→0` no PostgreSQL.

OpenAPI e catálogo de eventos foram regenerados e os nove testes de contrato ficaram verdes. O rebase incorporou FE-4 e o logo oficial sem conflito; hashes protegidos após o rebase: `frontend=74963b29dcecc61cef547434ba08a1c3094cbdef`, `docs/frontend=7c97395dafad8034a21f571514baba3c65d06504`. O diff funcional contra `origin/develop` não alterou essas áreas.

## Gate

`tools/backend/verify.sh` passou após o rebase: restore locked, format, build Release com 0 warnings/0 errors e 131/131 testes (`Unit 86`, `Integration 23`, `Contract 9`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). O teste PostgreSQL gerenciado passou em 8 s; cleanup final deixou zero container, volume ou network `com.harness.managed=true`.
