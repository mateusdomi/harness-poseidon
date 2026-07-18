# ADR-013 — Contrato REST/OpenAPI e SignalR

- Status: aceito
- Data: 2026-07-18

## Decisão

REST usa `/api/v1`, ULID, camelCase, cursor, UTC ISO-8601 e RFC 7807. Um hub SignalR em `/hubs/events` publica envelope sequenciado por stream e oferece snapshot+delta. OpenAPI gerado pelo Host e catálogo de eventos backend são canônicos após implementação.

## Consequências

Contratos provisórios da Kimi são reconciliados, nunca editados silenciosamente. Testes bloqueiam drift. Nome exibível vem de metadado `productName`, não do codinome hardcoded.
