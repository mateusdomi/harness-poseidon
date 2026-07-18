# ADR-010 — Estratégia Git do produto e do repositório oficial

- Status: aceito
- Data: 2026-07-18

## Decisão

O repositório oficial usa exclusivamente `main` e `develop`; desenvolvimento ocorre em `develop`, sem force push e sem merge em `main` sem autorização humana. Projetos administrados pelo produto podem usar branch por tarefa e worktree por tentativa.

## Consequências

PoCs Git usam somente fixtures descartáveis. Commits de projetos administrados usam Conventional Commits e trailers de tarefa, tentativa e agente; merges dependem de gates e claims de escopo.
