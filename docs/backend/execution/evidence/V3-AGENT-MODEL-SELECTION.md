# V3 — seleção persistida de conta, modelo, effort e fallback por agente

Data: 2026-07-19. Resultado: verde.

`PATCH /api/v1/agents/{id}/selection` persiste na instância tenant-scoped a conta, modelo,
effort canônico, valor traduzido para o provider, fallbacks e motivo auditável. A mutation aceita
somente agente idle/waiting, conta ativa, modelo/fallbacks habilitados do mesmo provider e effort
presente no mapping do modelo. IDs duplicados, fallback igual ao primário e strings arbitrárias
são recusados. Ledger e Outbox são gravados na mesma transação.

Migration dual `0039_agent_model_selection`; HTTP/restart e behavior provider-neutral cobrem
SQLite/PostgreSQL. Gates: `verify.sh` exit 0, 248/248 backend, 362/362 frontend, build Release
0 avisos/0 erros; `verify-sast.sh`, 30 regras/316 alvos/zero achado. Frontend preservado.
