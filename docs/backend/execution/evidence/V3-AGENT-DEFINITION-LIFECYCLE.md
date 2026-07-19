# V3 — CRUD e lifecycle de definições de agentes

Data: 2026-07-19. Resultado: verde.

Definições canônicas globais permanecem imutáveis; definições customizadas são isoladas por tenant.
A API cobre criação, edição com versão esperada, duplicação, enable/disable, archive tombstone,
listagem com `includeArchived` e exclusão somente quando nunca usada. Cada criação/edição grava
snapshot imutável em `agent_definition_versions`, ledger e Outbox na mesma transação.

O contrato inclui persona, missão, princípios operacionais, entregáveis, critérios de qualidade,
estilo de comunicação, limitações, skills, tools, modelo padrão, versão e lifecycle. Migration dual
`0040_agent_definition_lifecycle`; HTTP e behavior provider-neutral cobrem SQLite/PostgreSQL.

Gates: `verify.sh` exit 0, 248/248 backend, 362/362 frontend, build Release sem avisos/erros;
`verify-sast.sh`, 30 regras/316 alvos/zero achado. Frontend preservado.
