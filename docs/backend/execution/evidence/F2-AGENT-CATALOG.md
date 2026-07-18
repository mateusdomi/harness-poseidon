# Evidência F2-ORCH-1a — catálogo e instâncias de agentes

Data: 2026-07-18. Commit funcional publicado: `e815ca8` em `develop`.

A migration SQLite `0018_agent_catalog` semeia exatamente as seis definições exigidas pela missão: Chief Orchestrator, Product/Requirements Analyst, Software Architect, Software Engineer, Critic/QA e Technical Writer. Definições expõem papel, especialidade, modelo padrão e vínculos de skills/tools; instâncias preservam projeto, estado, tarefa atual, override de modelo, métricas, heartbeat e lease com fencing.

A criação de projeto agora insere o Chief referenciado por `chiefAgentId` na mesma transação do projeto, ledger e Outbox. Bancos existentes são migrados com backfill seguro para projetos cujo Chief já era um ULID. As APIs paginadas `agent-definitions` e `agents` implementam list/read, isolamento por tenant e filtro por projeto sem alterar os contratos do frontend.

O cenário HTTP comprovou as seis definições em ordem canônica, a instância única vinculada ao projeto, lease inicial com fencing 1 e recuperação de projeto+Chief após reinício do Host. O drift test compara os 9 campos de `AgentDefinition` e os 10 campos de `Agent` com o OpenAPI e os schemas TypeScript.

`tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 135/135 testes (`Unit 87`, `Integration 24`, `Contract 11`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem alterações do backend.
