# F2-PRJ-1 — Projetos pessoais

Data UTC: 2026-07-18

## Fatia vertical executada

- O módulo Projects materializa os 18 campos de `projectSchema`, com enums fechados, IDs ULID, chave normalizada, defaults pessoais e marca herdável.
- Configuração de repositório, branch, tecnologias e marca possui `configVersion` própria; nome/descrição/estado/criticidade/membros alteram somente a versão OCC interna.
- Migration SQLite `0011_projects` amplia a fundação com configuração, chefe lógico, modo operacional, atividade e soft-delete; chave é única por organização sem distinção de caixa.
- `SqliteProjectStore` filtra todas as operações por tenant, valida a organização do mesmo tenant, usa OCC e persiste create + ledger encadeado + Outbox `project.created` na mesma transação.
- A API publica list/read/create/update/delete, cursor, sessão pessoal e Problem Details. Delete é recuperável no banco por tombstone e deixa de projetar o recurso na API.
- Criação gera o identificador do chefe lógico, inicia em modo `manual`, estado `active`, `configVersion=1` e inclui o perfil criador quando membros são omitidos.
- O Outbox worker entregou `project.created` no stream persistido `project:<id>`; snapshot confirmou sequência e payload após a criação.
- OpenAPI e drift test conferem os 18 campos e os cinco verbos com os contratos TypeScript, sem editar o frontend.

## Evidência executada

O teste integrado recusou sessão ausente e organização inexistente, criou projeto, recusou chave duplicada, comprovou que rename não altera `configVersion`, comprovou que configuração a incrementa, recebeu o evento realtime, reiniciou o Host no mesmo banco, recuperou o projeto e validou o soft-delete.

```text
tools/backend/verify.sh
exit code: 0
Release build: 0 warnings, 0 errors
Unit: 77/77
Integration: 20/20
Contract: 6/6
Recovery: 4/4
Architecture: 6/6
Concurrency: 3/3
Total: 116/116
```

Migrations: SQLite `11→0`; PostgreSQL permanece `9→0`. Nenhum processo ou recurso Docker Harness ficou órfão.
