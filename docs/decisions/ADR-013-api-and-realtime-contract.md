# ADR-013 — Contrato REST/OpenAPI e SignalR

- Status: aceito
- Data: 2026-07-18

## Decisão

REST usa `/api/v1`, ULID, camelCase, cursor, UTC ISO-8601 e RFC 7807. Um hub SignalR em `/hubs/events` publica envelope sequenciado por stream e oferece snapshot+delta. OpenAPI gerado pelo Host e catálogo de eventos backend são canônicos após implementação.

Clientes assinam uma ou mais streams pelo método `Subscribe`; o Host mapeia cada stream a um grupo SignalR. O método cliente único é `event`. O publisher primeiro persiste/anexa o envelope e só então o transmite. `sequence` começa em 1 e cresce de forma independente por stream. Após lacuna, `GET /api/v1/event-streams/snapshot?stream=&afterSequence=` devolve a sequência atual, o último envelope por tipo e o delta ordenado depois do cursor.

Na PoC-7 o store é in-memory para isolar o protocolo. A Fase 1 move sequência/eventos para a autoridade relacional e Outbox sem alterar os contratos. Erros REST usam `application/problem+json`; detalhes SignalR ficam habilitados somente em Development.

`docs/contracts/openapi.json` é exportado mecanicamente pelo Host através de `tools/backend/export-contracts.sh`; servidores dinâmicos são removidos e título/tags são determinísticos. Drift tests comparam o documento publicado ao Host em execução e `events.json` ao catálogo tipado.

## Consequências

Contratos provisórios da Kimi são reconciliados, nunca editados silenciosamente. Testes bloqueiam drift. Nome exibível vem de metadado `productName`, não do codinome hardcoded. Retenção/paginação do delta e persistência entram na Fase 1.
