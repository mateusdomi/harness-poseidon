# ADR-002 — .NET 10 com toolchain local reproduzível

- Status: aceito
- Data: 2026-07-18

## Decisão

Todos os projetos miram `net10.0`, C# 14, nullable e analisadores como erro. O SDK 10.0.302 é fixado em `global.json` e instalado localmente por `tools/backend/install-dotnet.sh`; comandos usam `tools/backend/dotnet.sh`.

## Consequências

O host não depende do SDK global. CLI home e cache NuGet ficam no tooling ignorado do clone. Builds usam lockfiles e zero warnings novos.
