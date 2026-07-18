# F2-WORK-1a — Cadeia e quadro: criação e leitura

Data UTC: 2026-07-18

## Fatia vertical executada

- A cadeia F1 permanece autoridade única. A migration SQLite `0013_work_board_projection` amplia suas próprias tabelas com a projeção de produto exigida pelo frontend, sem criar um agregado concorrente.
- Solicitação, demanda, tarefa, instrução, tentativa e evento de tentativa têm contratos e APIs list/read tenant-scoped, paginação por cursor e filtros usados pelo quadro.
- Solicitações, demandas e tarefas podem ser criadas pelas application services autorizadas. Tarefa e instrução v1 commitam juntas; toda criação relevante entra no ledger.
- Demandas originadas de conversa e tarefas ainda sem demanda pública preservam as FKs obrigatórias da cadeia por elos internos marcados e ocultos das listagens. O contrato continua retornando `solicitationId`/`demandId = null` como publicado.
- `board_state` materializa as oito colunas sem enfraquecer o lifecycle técnico de quatro estados, actor–critic, evidência ou fencing já verdes.
- Prioridade, assignee, bloqueio, prazo, autoria da instrução e métricas de tentativa foram incorporados ao mesmo schema. Progresso continua objetivo e mantém executado/validado/aprovado separados.
- O cockpit passou a contar diretamente as oito colunas da projeção, incluindo backlog, corrections, tests/gates e blocked.
- `demand.created` e `task.created` saem com payload completo do frontend e `projectId` de roteamento; Outbox/ledger/dados são atômicos e o stream persistido confirmou as quatro criações.
- Os eventos SQLite do lifecycle F1 foram reconciliados para payloads tipados de `attempt.started`, `attempt.completed` e `task.stateChanged`; review deixa de usar o evento semântico incorreto `gate.changed`.
- OpenAPI e drift tests verificam 58 campos em seis recursos, sem alterar `frontend/**` ou `docs/frontend/**`.

## Evidência executada

O teste integrado criou solicitação, duas demandas (uma sem solicitação pública), duas tarefas (uma sem demanda pública), comprovou ocultação dos elos internos, instrução v1, tentativas/eventos vazios, backlog do cockpit, payloads realtime completos e recuperação após restart.

```text
tools/backend/verify.sh
exit code: 0
Release build: 0 warnings, 0 errors
Unit: 82/82
Integration: 22/22
Contract: 8/8
Recovery: 4/4
Architecture: 6/6
Concurrency: 3/3
Total: 125/125
```

Migrations: SQLite `13→0`; PostgreSQL permanece `9→0`. Nenhum processo ou recurso Docker Harness ficou órfão.
