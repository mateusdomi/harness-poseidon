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

## Release Candidate 2 — commit `709037b0fb78ddbdd06a04f2986ef337b4f5e28e`

A RC anterior (`d11df779`) reprovou GNG-3 por skeleton infinito no Cockpit sem projeto (causa
raiz frontend, ver `evidence/RC-FIRST-RUN-P1.md`). A RC2 integra o fix frontend publicado
`12b04a929c33119ca70cd39d02e009bfdd446a3f` (ancestral do commit do pacote) e corrige, em
`tools/backend/**`, dois blockers de empacotamento descobertos ao exercitar o gate first-run
contra o pacote real (`frontend/**` e `docs/frontend/**` intactos):

- UI de governança P1/P2 vinha desligada no pacote (`build-frontend.sh` sem
  `VITE_GOVERNANCE_CONTRACT_UI=on`), caindo no audit timeline legado;
- o gate first-run travava 30s lendo `<link rel="icon">` inexistente no SPA, cujo favicon é
  servido pelo Host em `/favicon.ico`.

Resultado (todos os gates exit 0, `release-manifest.json` como autoridade):

- `verify.sh`: frontend `444/444`, backend `301/301` (Unit 162, Integração 93, Contrato 30,
  Recovery 6, Arquitetura 7, Concorrência 3), build Release `0 Aviso(s)`/`0 Erro(s)`;
- governance linter `0/0`, SAST `0 findings`, secret scan limpo, SBOM; npm audit produção
  `0 vulnerabilities`; drift OpenAPI/eventos verde;
- Playwright mock `56/56`, a11y `42/42`, Storybook verde, Host real `3/3`;
- pacote self-contained: data dir vazio, navegador limpo, `package first-run real sem demo`
  **verde** (175 respostas, 5 WebSockets, Cockpit sem skeleton, favicon `image/png`, cookie
  inválido recuperado, governança P1/P2 visível), persistência pós-restart verde, `--demo`,
  doctor/logs/restart/stop e inventário Docker sem órfão.
- Validação externa em `~/Poseidon-RC2` (porta 5098, fora do repositório, data dir vazio):
  install/doctor/start/status/logs/restart/stop verdes; smoke de browser first-run e
  persistência pós-restart (`readiness: personal-session`) verdes; sem processo residual.

Pacote RC2: `.artifacts/release-candidate/709037b0fb78-osx-arm64/poseidon-709037b0fb78-osx-arm64.tar.gz`
(`90309962` bytes, SHA-256 `a32f0241c8ae74718385dfd92961d8816a43404496f540f6f89626fd16cce2a4`).

## Critério de recomendação

Recomendação técnica para a RC2 (`709037b`): **Go para nova homologação humana**. Todos os gates
automáticos terminaram verdes, o skeleton infinito que reprovou a RC anterior está corrigido e
comprovado no pacote (empacotado e instalado externamente), e a UI de governança P1/P2 é
embarcada e visível. Isso **não** é Go de produção nem declaração de GNG-3: os aceites humanos
GNG-3/GNG-4/GNG-6, assinatura Apple e integrações reais declaradas nas notas continuam externos e
dependem de teste humano/credenciais.

## Riscos residuais

- pacote ad-hoc sem Developer ID/notarização;
- experiência ainda não aceita por uma pessoa em Mac limpo;
- Entra ID, Teams e provider/modelo reais dependem de contas/credenciais;
- token Telegram herdado requer rotação externa antes de novo smoke;
- diferença histórica entre WorkChain de negócio e tentativa técnica exige observabilidade clara,
  embora suas autoridades e eventos estejam separados e testados.

O roteiro de 30–60 minutos está em `docs/backend/operations/HOMOLOGATION.md`.
