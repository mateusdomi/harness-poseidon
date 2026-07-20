# Relatório da Release Candidate

## Escopo técnico

A candidata fecha o P2 de aprendizado seguro, a supervisão Host/Runner, a experiência de comando
único e o processo reproduzível de empacotamento. O artefato final e o relatório de execução são
gerados em `.artifacts/release-candidate/<sha>-osx-arm64/` por `./poseidon release-candidate` a partir
de uma working tree limpa em `develop`.

## Resultado observado em 2026-07-20

- `tools/backend/verify.sh`: exit 0; frontend `437/437`, backend `299/299`, build Release com
  `0 Aviso(s)` e `0 Erro(s)`;
- drift de OpenAPI e catálogo de eventos: verde;
- governance linter: `errors=0 warnings=0`; SAST: `0 findings`; secret scan: nenhum segredo de alta
  precisão; dependências frontend de produção: `0 vulnerabilities`;
- Playwright mock: `56/56`; a11y dedicado: `42/42`; Storybook: build verde;
- Host real empacotado: `3/3` em desktop 13” dark, tablet light e mobile dark, com 15 screenshots,
  sem erro de console nem asset 404;
- pacote self-contained instalado em diretório limpo e validado exclusivamente por seus binários:
  `start`, `status`, `doctor`, `logs`, `restart` e `stop` verdes;
- primeiro uso vazio e `--demo`, checksums, SBOM e inventário Docker final verdes; zero recurso com
  `com.harness.managed=true` ficou órfão.

O `release-manifest.json` dentro do diretório da RC é a autoridade para SHA, nome, tamanho e
SHA-256 do pacote. `TEST_REPORT.md`, `gates.log` e `screenshots/` preservam a evidência bruta.

## Critério de recomendação

Recomendação técnica: **Go para homologação**. Todos os gates automáticos do comando terminaram
verdes. Isso não é Go de produção: os aceites humanos GNG-3/GNG-4/GNG-6, assinatura Apple e
integrações reais declaradas nas notas continuam externos.

## Riscos residuais

- pacote ad-hoc sem Developer ID/notarização;
- experiência ainda não aceita por uma pessoa em Mac limpo;
- Entra ID, Teams e provider/modelo reais dependem de contas/credenciais;
- token Telegram herdado requer rotação externa antes de novo smoke;
- diferença histórica entre WorkChain de negócio e tentativa técnica exige observabilidade clara,
  embora suas autoridades e eventos estejam separados e testados.

O roteiro de 30–60 minutos está em `docs/backend/operations/HOMOLOGATION.md`.
