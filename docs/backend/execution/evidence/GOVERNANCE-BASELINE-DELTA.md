# Governance — baseline e matriz de delta pré-G0

Data da auditoria: 2026-07-20. Estado auditado: `origin/develop` em
`2e9928ffac536dbde843a9b683e68e865e6c4f0b` (`docs(research): add agent
governance study`). Esta evidência foi criada antes da Onda G0 e não afirma que
nenhuma entrega de G0–G8 esteja concluída.

## Baseline reproduzível

- `git fetch origin && git rebase origin/develop`: branch `develop` já estava
  sincronizada e a working tree estava limpa.
- Comando de tag local, não publicada:
  `git tag -a governance-baseline-20260720-2e9928f 2e9928ffac536dbde843a9b683e68e865e6c4f0b -m "Governance baseline before G0 (2026-07-20)"`.
- Bundle completo fora do repositório:
  `$POSEIDON_BACKUP_DIR/governance-baseline-20260720-2e9928f.bundle`.
  SHA-256: `d1681c14ac4b79c6391949dc4ea0cb48c149e58a4fe118920acc87c834749b23`.
  `git bundle verify` confirmou histórico completo e sete refs.
- O scanner de alta precisão passou nos arquivos rastreados, em todos os diffs
  do histórico e nos três prompts históricos antes da importação. Nenhum valor
  de segredo foi emitido ou registrado.
- O primeiro `tools/backend/verify.sh` falhou porque o daemon Docker estava
  desligado. Com Docker Desktop 29.5.2 ativo, a repetição terminou com exit code
  `0`.
- Resultado verde: frontend 47 arquivos/410 testes; backend 123 unitários, 82
  integração, 28 contrato, 7 arquitetura, 5 recovery e 3 concorrência (248 no
  total); build Release com 0 erros e 0 warnings C#.
- Warnings herdados observados fora do compilador: três avisos de depreciação
  npm, oito vulnerabilidades npm reportadas (6 moderadas, 1 alta, 1 crítica),
  `ExperimentalWarning` do ambiente de teste Node e duas anotações PURE
  descartadas pelo Rollup. São dívida de baseline, não foram classificados como
  gate verde de produção desta missão.
- O inventário encontrou um conjunto de teste Harness órfão e parado, rotulado
  `com.harness.managed=true`, do attempt `poc8-0ecf145e117a`: um container, um
  volume e uma rede. Os três recursos foram removidos; recursos de terceiros
  permaneceram intactos. O inventário gerenciado terminou vazio.
- Árvore frontend de referência: `e73b170f9ed0e9c4a826d751541a20e922529967`.

Hashes SHA-256 dos originais externos antes da sanitização:

| Prompt | SHA-256 original |
|---|---|
| v1.3 + adendo | `fd863025a99a9fc12bc009ab29f3b2eb7cc0882f99b9aed31970f7b1e34b8cd4` |
| continuidade v2.0 | `56a077c6346c12ffffc1342cd8723dc99d772ebec542b28055754e29fe1910bf` |
| continuidade v3.0 | `efea9b5b2ebc15bdbf64b10412447246945188e5489a3aec7aeac99cc465b6cb` |

## Matriz de delta

| Capacidade aprovada | Evidência no estado atual | Estado pré-G0 | Onda/gate |
|---|---|---|---|
| Prompts históricos versionados, sanitizados e não auto-loaded | Os três prompts normativos existem somente fora do Git; `CURRENT_STATE.md` referencia um diretório local legado | Ausente / risco L1 confirmado | G0 / P0 |
| Threat model canônico único | `docs/security/THREAT_MODEL.md` e `docs/backend/security/THREAT_MODEL.md` divergem; o segundo é mais recente e abrangente | Conflito real | G0 / P0 |
| Documentos ativos portáveis | `CURRENT_STATE.md` contém caminhos de usuário, clone e Downloads | Não conforme | G0 / P0 |
| Estado factual versus bloqueios/suposições manuais | `CURRENT_STATE.md` mistura fatos, instruções, suposições e bloqueios sem marcadores de geração | Parcial; continua manual | G0 / P0 |
| Núcleo canônico e regras temáticas | Não existe `governance/` | Ausente | G1 / P0 |
| Manifest YAML validado por JSON Schema | Não existe manifest nem schema; há 171 arquivos Markdown rastreados | Ausente | G1–G2 / P0 |
| Adapters pequenos e gerados | Não existem `CLAUDE.md` nem `AGENTS.md`; não há evidência para criar `KIMI.md` | Ausente | G1 / P0 |
| Índice documental gerado | Não existe `docs/INDEX.md`; os documentos não possuem grafo navegável canônico | Ausente | G1 / P0 |
| Linter documental com fixtures negativas | Nenhum projeto/script implementa as regras da missão | Ausente | G2 / P0 |
| Enforcement em verify, CI e release candidate | `verify.sh` e `verify-release-candidate.sh` existem, mas não há diretório `.github/` nem job CI no SHA auditado | Parcial | G2 / P0 |
| Escopo de paths por agente no runtime/CI | Claims duráveis protegem projetos administrados; não existe policy por provider para Kimi/backend/shared no runtime do Poseidon | Parcial, fronteira reutilizável | G3 / P0 |
| Segredos: tracked/staged/logs/args + redaction + referências | `scan-secrets.sh` cobre somente padrões de alta precisão nos arquivos rastreados; há redaction localizada e `ISecretStore`, mas o incidente R-013 prova lacuna em argumentos/processos | Parcial | G3 / P0 |
| Runbook de rotação e regressão R-013 | R-013 está documentado no estado/riscos, sem teste mecânico específico nem runbook dedicado de rotação | Ausente | G3 / P0 |
| Context Bundle Builder determinístico e cacheado | O Chief reconstrói `StatusDigest`, porém não seleciona documentos pelo manifest nem produz bundle reproduzível | Ausente | G4 / P1 |
| Receipt em 100% dos turnos do Chief | Existem receipts de Inbox/IPC e ledger, mas nenhum receipt de contexto com documentos/checksums/tokens | Ausente | G4 / P1 |
| Métricas sem inferir uso por menção | Não há eventos tipados para seleção, entrega, abertura, regra acionada, violação e truncamento | Ausente | G4 / P1 |
| Fresh-context evaluator read-only e Default-FAIL | O domínio exige critic independente a partir de risco médio, mas não há executor separado com contexto limpo e ferramentas read-only | Parcial conceitual; implementação ausente | G5 / P1 |
| Findings P0–P3 e verdict estruturado | Reviews atuais não expõem o contrato completo de severidade/confidence/evidence/range/rule/action/verdict | Ausente | G5 / P1 |
| Hashline/stale-patch atômico com benchmark | OCC existe nos agregados e documentos, mas não há âncora por hash pré-escrita para edição de arquivos | Ausente | G6 / P1 |
| Stale-doc detector | Não há detector para due date, source drift, enforcement, seleção/truncamento ou concorrência | Ausente | G7 / P1 |
| `OmpRpcAgentExecutor` opcional de produção | Catálogo possui Fake e Codex; ADR-008 mantém outros adapters no backlog; `omp` não é dependência | Ausente | G8 / P1 |
| Contratos/eventos OpenAPI | OpenAPI/eventos existentes cobrem o produto atual, não as capacidades G4–G8 | Base existente; extensão necessária | G4–G8 / P1 |
| Persistência dual e recovery | SQLite/PostgreSQL, OCC, migrations separadas e suites de recovery já existem | Fundação presente | Reutilizar em G4–G8 |
| Feature flags operacionais de rollback | Há modo tipado para execução isolada; não há flags específicas das capacidades de governança | Parcial | G4–G8 / P1 |
| Learning candidates com promoção gated | Não existe e está explicitamente fora do escopo até autorização P2 | Ausente por decisão | P2, não iniciar |

## Decisões preservadas

- Enforcement autoritativo ficará em `tools/backend/verify.sh`, CI e policy de
  runtime; hooks locais serão apenas conveniência instalável.
- Não será criado `KIMI.md` sem prova do mecanismo de carregamento.
- Não serão criados os arquivos avulsos proibidos nem importadas skills/personas
  do ECC em massa.
- `frontend/**` e `docs/frontend/**` permanecem fora do escopo de escrita.
- G0 começa somente após esta matriz; nenhuma linha da tabela conta como entrega
  concluída sem execução e evidência posterior.
