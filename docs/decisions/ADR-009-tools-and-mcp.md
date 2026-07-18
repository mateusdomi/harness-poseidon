# ADR-009 — Ferramentas tipadas e MCP estável

- Status: aceito
- Data: 2026-07-18

## Decisão

Ferramentas internas são interfaces .NET com schemas, allowlist e policy check pré-execução por fase/risco. Integrações externas usam MCP estável 2025-11-25; recursos RC 2026-07-28 ficam atrás de flag experimental.

## Consequências

Plugins são versionados com checksum, permissões e risk tier. Nenhum comando de agente cruza a política apenas por sugestão do modelo.
