# Evidência — Governance Gate P0

Data: 2026-07-20. Owner: Platform Governance. Branch: `develop`.

## Baseline recuperável

- SHA auditado antes de qualquer edição: `2e9928ffac536dbde843a9b683e68e865e6c4f0b`.
- Tag anotada somente local: `governance-baseline-20260720-2e9928f`; comando registrado:
  `git tag -a governance-baseline-20260720-2e9928f 2e9928f -m "Governance baseline 2026-07-20"`.
- Bundle externo: `$REPO_PARENT/harness-poseidon-backups/governance-baseline-20260720-2e9928f.bundle`;
  SHA-256 `d1681c14ac4b79c6391949dc4ea0cb48c149e58a4fe118920acc87c834749b23`.
- `git bundle verify` confirmou bundle completo; scans de alta precisão no checkout, histórico, bundle
  e fontes importadas não encontraram segredo. A tag não foi publicada.

## G0 — fonte de verdade

- Os três prompts históricos foram sanitizados, versionados em `governance/prompts/` e marcados
  `loadPolicy: never`. Checksums dos originais foram preservados somente como hash:
  `fd863025a99a9fc12bc009ab29f3b2eb7cc0882f99b9aed31970f7b1e34b8cd4`,
  `56a077c6346c12ffffc1342cd8723dc99d772ebec542b28055754e29fe1910bf` e
  `efea9b5b2ebc15bdbf64b10412447246945188e5489a3aec7aeac99cc465b6cb`.
- `docs/backend/security/THREAT_MODEL.md` é o único threat model canônico;
  `docs/security/THREAT_MODEL.md` é redirect histórico superseded e a decisão está no ADR-015.
- A busca em documentos ativos não encontrou path pessoal, nome de máquina, path temporário nem
  referência normativa a Downloads. `CURRENT_STATE.md` declara seções manual e factual sem fingir
  geração automática.

## G1–G3 — entrega e enforcement

- `governance/core.md`, seis regras, manifest validado por JSON Schema e templates são fontes
  versionadas. `CLAUDE.md`, `AGENTS.md` e `docs/INDEX.md` são gerados e verificados por checksum;
  `KIMI.md` não foi criado.
- O linter cobre todos os erros e warnings definidos para P0, possui fixtures negativas e roda em
  `verify.sh`, release-candidate e job CI dedicado.
- A policy tipada de path rejeita Kimi fora de `frontend/**`/`docs/frontend/**`, rejeita backend
  dentro desses roots e exige claims persistidos para compartilhados. O runtime bloqueia antes de
  adquirir workspace; o teste de integração prova ausência de abertura do sandbox. O CI rejeita
  diff misto.
- O secret gate cobre arquivos, staged diff, logs e argumentos de processo sem imprimir valores.
  Redaction central, proibição de credential switches, regressão sintética R-013 e runbook dedicado
  estão testados. Flags operacionais: `PathScopePolicyEnabled` e `HARNESS_SECRET_SCAN_MODE`.

## Execução real do gate

| Verificação | Resultado |
|---|---|
| `tools/backend/verify.sh` | verde |
| Linter documental | `errors=0`, `warnings=0` |
| Build .NET Release | 0 erros, 0 warnings |
| Frontend protegido | 47 arquivos de teste, 410 testes verdes; audit de produção 0 |
| Backend | 146 unit + 83 integration + 28 contract + 7 architecture + 5 recovery + 3 concurrency = 272 verdes |
| Secret scan full | verde, valores omitidos por design |
| Frontend tree | SHA `e73b170f9ed0e9c4a826d751541a20e922529967`, idêntico ao baseline |
| Docker gerenciado órfão | zero container, zero volume, zero network |

O primeiro gate integral detectou uma regressão de composição: a classificação de agente era
exigida também em repositórios externos. A correção limita fail-closed a repositórios Poseidon com
manifest; o conjunto dogfood/endpoint/orquestração foi reexecutado verde antes da suíte completa.
Nenhum recurso de produção ou segredo real foi usado.
