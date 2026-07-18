# ADR-014 — ArchUnitNET com verificação estrutural complementar

- Status: aceito
- Data: 2026-07-18

## Contexto

A missão permite ArchUnitNET ou equivalente estável. Pesquisa em 2026-07-18 encontrou a release estável 0.13.3 publicada em 2026-03-05 e os pacotes `TngTech.ArchUnitNET.xUnit`/`xUnitV3`. A solução usa xUnit v2 neste ciclo.

## Decisão

Usar `TngTech.ArchUnitNET.xUnit` 0.13.3 para dependências entre assemblies e testes XML determinísticos para topologia de projetos. O método `Check` do adapter exige `using ArchUnitNET.xUnit`, detalhe ausente do exemplo principal e coberto pelo teste compilado.

## Consequências

ArchUnitNET analisa bytecode; regras que dependem de debug artifacts poderão rodar em Debug. Regras de arquivos/projetos continuam complementares, pois dependências centrais em `Directory.Build.targets` não aparecem diretamente em cada `.csproj`.

## Fontes verificadas

- Release oficial: https://github.com/TNG/ArchUnitNET/releases/tag/0.13.3
- Pacote oficial: https://www.nuget.org/packages/TngTech.ArchUnitNET.xUnit/0.13.3
- README da tag: https://github.com/TNG/ArchUnitNET/blob/0.13.3/README.md
