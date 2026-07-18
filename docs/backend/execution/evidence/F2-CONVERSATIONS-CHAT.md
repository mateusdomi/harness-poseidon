# F2-CHAT-1 — Conversas, mensagens e streaming durável

Data UTC: 2026-07-18

## Fatia vertical executada

- O módulo Conversations materializa os contratos exatos de conversa e mensagem do frontend, com estados `active|archived`, autores fechados, ULIDs, UTC e limites objetivos.
- A migration SQLite `0012_conversations` cria conversas, mensagens e turnos, com FKs tenant/projeto, autoria coerente por role, soft-delete e índices de leitura por conversa.
- `SqliteConversationStore` mantém list/read/create/delete tenant-scoped; criação/arquivamento entram no ledger e mensagens atualizam `lastMessageAt` sob o dispatcher único.
- `POST /api/v1/conversations/{conversationId}/turns` responde `202` com o handle e persiste, numa transação, mensagem humana, resposta determinística do Chief, turno concluído, auditoria e todos os envelopes da Outbox.
- A resposta desta fatia é determinística e sem rede/cota, adequada ao executor Fake. O pipeline completo do Chief, mailbox, structured output e executores produtivos permanece na fatia própria de orquestração.
- A ordem entregue pelo dispatcher é a mesma do mock publicado: `message.appended` humano, `chat.turnStarted`, chunks indexados, `message.appended` do Chief e `chat.turnCompleted`.
- `OutboxRealtimeStreamResolver` agora resolve tanto `conversationId` no topo quanto dentro de `message`, portanto todos os eventos chegam ao stream `conversation:<id>` sem adicionar campos fora do contrato.
- A API publica paginação e filtros de conversas/mensagens, leitura individual, criação, remoção recuperável e turno; sessão ausente/inválida, projeto/conversa ausentes e estado inativo retornam Problem Details.
- OpenAPI e drift test conferem rotas, campos e eventos com os contratos TypeScript, sem alterar `frontend/**` ou `docs/frontend/**`.

## Evidência executada

O teste integrado recusou sessão ausente e projeto inexistente, criou conversa e mensagem direta, iniciou um turno, recuperou sete eventos em sequência `1..7`, confirmou as duas mensagens humanas e a mensagem do Chief, reiniciou o Host no mesmo banco, recuperou o histórico e validou o soft-delete.

```text
tools/backend/verify.sh
exit code: 0
Release build: 0 warnings, 0 errors
Unit: 80/80
Integration: 21/21
Contract: 7/7
Recovery: 4/4
Architecture: 6/6
Concurrency: 3/3
Total: 121/121
```

Migrations: SQLite `12→0`; PostgreSQL permanece `9→0`. Nenhum processo ou recurso Docker Harness ficou órfão.
