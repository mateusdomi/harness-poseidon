# F2-CPK-1 — Cockpit e StatusDigest determinístico

Data UTC: 2026-07-18

## Fatia vertical executada

- `GET /api/v1/projects/{projectId}/status-digest` publica uma projeção consistente e tenant-scoped para cockpit e reconstrução de contexto do chefe.
- O read model deriva progresso executado/validado/aprovado dos objetivos ponderados do último workflow; nenhuma porcentagem vem de LLM e as três trilhas permanecem separadas.
- Estados da cadeia fundacional são projetados nas oito colunas do frontend; contagens ausentes permanecem zero, sem criar estado sintético.
- O digest inclui aprovações pendentes, fase ativa, gates pendentes, atividade recente do ledger, próxima ação determinística e `asOf` derivado do estado persistido.
- O fingerprint SHA-256 cobre o digest completo sem o próprio hash; duas leituras do mesmo estado retornam o mesmo valor.
- Como as autoridades de agentes e budgets ainda não foram materializadas na Fase 2, o contrato declara `unavailableSignals=[agents,budgets]` e não fabrica saúde/custo. As fatias próprias substituirão esses sinais por dados reais.
- O teste integrado semeou uma cadeia real pelo `SqliteWorkChainStore`, e o digest recompôs a tarefa pronta e a atividade `task.created`.
- A investigação corrigiu um drift latente nos dois providers: `workChain.created` não fazia parte do catálogo realtime. SQLite e PostgreSQL agora emitem o canônico `task.created` com `projectId`, permitindo roteamento pelo Outbox worker.
- OpenAPI publica a rota e todos os campos; frontend e docs protegidos permaneceram intactos.

## Evidência executada

```text
tools/backend/verify.sh
exit code: 0
Release build: 0 warnings, 0 errors
Unit: 78/78
Integration: 20/20
Contract: 6/6
Recovery: 4/4
Architecture: 6/6
Concurrency: 3/3
Total: 117/117
```

Migrations permanecem SQLite `11→0` e PostgreSQL `9→0`. Nenhum processo ou recurso Docker Harness ficou órfão.
