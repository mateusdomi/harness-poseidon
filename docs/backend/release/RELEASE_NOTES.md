# Poseidon Release Candidate — notas de release

Data: 2026-07-20. Canal: `develop`. Esta é uma candidata técnica para homologação, não uma release
publicada nem um aceite humano.

## Release Candidate 2 (`709037b0fb78`)

Sucede a RC `d11df779`, que reprovou GNG-3 por skeleton infinito no Cockpit sem projeto. A RC2
integra o fix frontend publicado `12b04a9` (estado vazio orientado quando não há projeto/sessão) e
embarca a UI de governança P1/P2 ligada no pacote. Correções de empacotamento ficaram restritas a
`tools/backend/**`; `frontend/**` e `docs/frontend/**` não foram alterados. Todos os gates
automáticos passaram e a candidata foi instalada e revalidada fora do repositório. GNG-3/GNG-4/GNG-6
seguem pendentes de aceite humano.

## Destaques

- Launcher pessoal e Runner em processos supervisionados, comando único `./poseidon`, estado/PIDs,
  porta loopback, logs, diagnóstico, stop/restart e pacote macOS self-contained.
- Primeiro uso real com dados demo opcionais por `./poseidon start --demo`, sem segredo e sem alterar
  bancos já inicializados.
- Governança P1 integrada e P2 completo para learning candidates: sete tipos fechados, evidência,
  deduplicação, review humano, avaliação independente, shadow, aprovação, promoção manual,
  monitoramento, rollback, depreciação, histórico e métricas.
- Paridade SQLite/PostgreSQL com OCC, Inbox, Outbox, ledger, tenant scope e eventos realtime.
- OpenAPI e catálogo de eventos 1.1 atualizados e protegidos por contract drift tests; transições P2
  chegam pelo evento canônico de auditoria, com ação e versão do candidate no payload.
- Processo `./poseidon release-candidate` com gates, frontend, Storybook, E2E/a11y, pacote,
  instalação/start/restart/stop real, SBOM, checksums, manifesto e relatórios.

## Compatibilidade e dados

- Pacote alvo atual: macOS Apple Silicon (`osx-arm64`), executável sem IDE, Node ou SDK .NET.
- O banco local é preservado no data dir; migrations idempotentes avançam SQLite e PostgreSQL até
  `0046`.
- Upgrade/rollback de pacote e backup/restore existentes permanecem suportados.

## Dependências externas declaradas

Developer ID/notarização, instalação humana em Mac limpo (GNG-4), Entra ID real, Azure Bot/Teams
real, provider/modelo que consuma cota e aceites GNG-3/GNG-6 não são simulados. O token Telegram deve
ser rotacionado no BotFather antes de qualquer novo smoke real.
