# ADR-0001 — Arquitetura definitiva do Poseidon

## Contexto

O Poseidon já possui monólito modular .NET, motor durável, persistência SQLite e
PostgreSQL, executores CLI, worktrees, ScopeClaims, outbox e ledger. O produto
precisa operar como desktop pessoal sem serviços obrigatórios e evoluir para modo
servidor multiusuário.

## Decisão

Adotar planos lógicos separados sobre o monólito existente:

- motor durável próprio como workflow engine;
- SQLite como fonte da verdade pessoal e PostgreSQL como fonte da verdade servidor;
- `IVectorIndex` com sqlite-vec ou pgvector, sempre derivado;
- `IArtifactStore` com filesystem versionado ou MinIO;
- executores CLI-first em worktrees e sandboxes;
- MCP para ferramentas e A2A apenas no roadmap;
- OpenTelemetry, Grafana stack e Langfuse para observabilidade;
- governança documental por manifest e Context Builder server-side;
- Bruna como única autoridade de publicação e sem tools de execução;
- capability tokens, PEP e revisão distinta para mudanças de código.

Redis não integra o baseline. Valkey só pode ser avaliado por contenção medida no
modo servidor. Temporal permanece opção futura atrás da porta do engine, não
dependência atual.

## Consequências

O modo pessoal permanece autônomo e o modo servidor reutiliza contratos e
migrations. A equipe mantém paridade entre providers e instrumenta o engine
existente. Vetores e artefatos não substituem o banco factual. Carga de contexto,
custos, transições e decisões tornam-se auditáveis.

Esta decisão é ratificada como base para a trilha de implementação e só pode ser
substituída por ADR aprovado.
