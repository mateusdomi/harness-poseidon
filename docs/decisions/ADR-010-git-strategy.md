# ADR-010 — Estratégia Git do produto e do repositório oficial

- Status: aceito
- Data: 2026-07-18

## Decisão

O repositório oficial usa exclusivamente `main` e `develop`; desenvolvimento ocorre em `develop`, sem force push e sem merge em `main` sem autorização humana. Projetos administrados pelo produto podem usar branch por tarefa e worktree por tentativa.

Operações de metadados Git de um mesmo repositório são serializadas, enquanto o trabalho dentro de worktrees distintas pode ocorrer em paralelo. A criação é idempotente: repetir `(branch, destino)` registrado retorna a mesma worktree; destino incompatível falha fechado. Todos os destinos precisam estar contidos na raiz controlada da execução e comandos usam argumentos estruturados, nunca shell.

Claims usam paths relativos normalizados e comparação por segmentos. Igualdade, ancestralidade e descendência conflitam; prefixos apenas textuais como `src/api/**` e `src/api-client/**` não conflitam. Um attempt não pode substituir claims ativos e a aquisição repetida do mesmo conjunto é idempotente. A persistência transacional dos claims entra na Fase 1; a PoC-5 valida semântica e integração Git.

## Consequências

PoCs Git usam somente fixtures descartáveis e fotografam refs/worktrees do Harness antes e depois. Commits de projetos administrados usam Conventional Commits e trailers de tarefa, tentativa e agente; merges dependem de gates e claims de escopo.
