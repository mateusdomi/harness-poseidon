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

## Release Candidate 3 — commit `bf08cc809f023f2bbaa2d3b3b19651c0604d6c4a`

A RC2 iniciou e resolveu o skeleton infinito, mas a homologação humana encontrou um novo
bloqueador P1: `GET /api/v1/governance-runtime/stale-doc-findings` retornava HTTP 500 durante
navegação normal. Causa raiz comprovada (logs da instalação humana + reprodução determinística de
CWD neutro): `StaleDocumentDetector` fazia `File.ReadAllBytes` de cada documento do manifest sem
checar existência; `frontend/README.md` (doc `audience: runtime`) é referenciado pelo manifest mas
não era empacotado (fonte sob `frontend/`), então a leitura lançava `DirectoryNotFoundException` não
tratada. Um segundo defeito — `ResolveGovernanceRoot` resolvia a partir do CWD antes do install dir —
fazia o gate anterior (iniciado com CWD=repo) vazar para o checkout de dev e mascarar o 500.

Correções (somente backend/`tools/**`; `frontend/**` e `docs/frontend/**` intactos):

- `StaleDocumentDetector`: fonte ausente vira finding tipado `SourceMissing` e pula as checagens de
  conteúdo; nunca lança;
- `Harness.Host.csproj`: empacota `frontend/README.md` (doc de governança de runtime) como Content;
- `ResolveGovernanceRoot`: resolve a partir do install dir (`AppContext.BaseDirectory`) antes do CWD,
  eliminando a dependência do checkout de desenvolvimento;
- novo gate `verify-governance-endpoints.mjs` + estágio em `verify-release-candidate.sh` iniciado de
  CWD NEUTRO: sessão vazia, todos os GETs de governança sem 500 (nominais 200; id inexistente 404;
  burst concorrente determinístico), Governança e Notificações sem console error/asset 404, antes e
  depois de restart, com corpos sanitizados como evidência.

Integra o fix frontend `142c6c72d48e11d004f18fe6982468c68645a941` (checkbox selecionado visível).

Resultado (todos os gates exit 0; `release-manifest.json` como autoridade):

- `verify.sh`: frontend `444/444`, backend `302/302` (Unit 163, Integração 93, Contrato 30,
  Recovery 6, Arquitetura 7, Concorrência 3), build Release `0 Aviso(s)`/`0 Erro(s)`;
- governance linter `0/0`, SAST `0`, secret scan, SBOM; npm audit produção `0 vulnerabilities`;
- Playwright mock `56/56`, a11y `42/42`, Storybook, Host real `3/3`;
- pacote self-contained: first-run real sem demo verde, persistência pós-restart, `--demo`;
- **governance endpoints (CWD neutro, sessão vazia e pós-restart): zero 500** — o defeito da RC2
  não reaparece; `stale-doc-findings` responde `200`;
- validação externa em `~/Poseidon-RC3-Human` (porta 5099, CWD neutro): install/doctor/start/status/
  logs/restart/stop verdes, first-run verde, governança sem 500, persistência pós-restart.

Pacote RC3: `.artifacts/release-candidate/bf08cc809f02-osx-arm64/poseidon-bf08cc809f02-osx-arm64.tar.gz`
(`90805897` bytes, SHA-256 `a0907275f1f81f4d85336663d92c33c5b6cfa43682e1f67bca5077b8c660027c`).

## Critério de recomendação

Recomendação técnica para a RC3 (`bf08cc8`): **Go para nova homologação humana**. O 500 de
governança que reprovou a RC2 está corrigido pela causa raiz e comprovado sem reaparecer em
instalação limpa de CWD neutro (empacotada e revalidada externamente); todos os GETs de governança
respondem sem 500. GNG-3/GNG-4/GNG-6, assinatura Apple e integrações reais continuam externos e
dependem de teste humano/credenciais; GNG-3 **não** foi declarado.

Histórico — recomendação técnica para a RC2 (`709037b`): **Go para nova homologação humana**. Todos os gates
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
