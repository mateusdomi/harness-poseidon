<!-- GENERATED FILE — DO NOT EDIT. source=governance/manifest.yaml version=2.0.0 checksum=sha256:b1d9128df65e02d2e31b9c7c78bee0f13a144b7fb6a238d9bc1b21747709d1c0 -->
# Poseidon documentation index

Generated from `governance/manifest.yaml`. Regenerate with `tools/backend/generate-governance.sh`.

## By authority

- `Adapter`: 2
- `Canonical`: 57
- `Generated`: 1

## By domain

- `adapter`: 2
- `agent`: 1
- `architecture`: 1
- `backend`: 4
- `contract`: 7
- `decision`: 9
- `entrypoint`: 1
- `governance`: 1
- `index`: 1
- `product-standard`: 12
- `rule`: 11
- `runbook`: 3
- `security`: 3
- `workflow`: 4

## By phase

- `*`: 45
- `architecture`: 8
- `development`: 1
- `homologation`: 1
- `planning`: 2
- `sustentation`: 2
- `triage`: 1
- `validate`: 1

## By status

- `Active`: 60

## By owner

- `Operations`: 7
- `Platform Engineering`: 26
- `Platform Governance`: 14
- `Product Security`: 8
- `Quality Engineering`: 5

## By load policy

- `Always`: 4
- `Bundle`: 33
- `OnDemand`: 23

## Documents by authority and domain

### Canonical

#### agent

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Bruna](agents/bruna.md) | * | Active | Platform Governance | Bundle | 2441 |

#### architecture

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Visão geral da arquitetura](architecture/overview.md) | * | Active | Platform Engineering | Bundle | 359 |

#### backend

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Context Builder](backend/context.md) | * | Active | Platform Governance | Bundle | 240 |
| [Observabilidade](backend/observability.md) | * | Active | Operations | Bundle | 219 |
| [Avaliações](backend/evals.md) | * | Active | Quality Engineering | Bundle | 213 |
| [Memória](backend/memory.md) | * | Active | Platform Engineering | Bundle | 246 |

#### contract

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Contrato de card](contracts/card.md) | * | Active | Platform Governance | Bundle | 609 |
| [Contrato de handoff](contracts/handoff.md) | * | Active | Platform Governance | Bundle | 245 |
| [Contrato de revisão](contracts/review.md) | * | Active | Quality Engineering | Bundle | 228 |
| [Contrato backend e frontend](contracts/api-frontend.md) | * | Active | Platform Engineering | Bundle | 191 |
| [Contrato de canais](contracts/channels.md) | * | Active | Platform Engineering | Bundle | 618 |
| [Contrato de eventos](contracts/events.md) | * | Active | Platform Engineering | Bundle | 195 |
| [Contrato de quota](contracts/quota.md) | * | Active | Operations | Bundle | 182 |

#### decision

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [ADR-0001 — Arquitetura definitiva do Poseidon](decisions/ADR-0001-arquitetura-definitiva-v3.md) | architecture | Active | Platform Engineering | OnDemand | 356 |
| [ADR-0002 — Onde a política de ferramentas é aplicada em executores CLI](decisions/ADR-0002-fronteira-de-ferramentas-em-executores-cli.md) | architecture | Active | Platform Engineering | OnDemand | 794 |
| [ADR-0003 — Memória conversacional e provedor](decisions/ADR-0003-memoria-conversacional-e-provedor.md) | architecture | Active | Platform Engineering | OnDemand | 864 |
| [ADR-0004 — Maturidade honesta das fases 6 a 9](decisions/ADR-0004-maturidade-honesta-das-fases-6-9.md) | architecture | Active | Platform Engineering | OnDemand | 828 |
| [ADR-0005 — Contenção desligada: decisão reversível para uso interno](decisions/ADR-0005-contencao-desligada.md) | architecture | Active | Product Security | OnDemand | 1121 |
| [ADR-0006 — Baseline de engenharia do produto entregue](decisions/ADR-0006-baseline-de-engenharia-do-produto-entregue.md) | architecture | Active | Platform Engineering | OnDemand | 1932 |
| [ADR-0007 — Escopo de path vazio na seleção de contexto](decisions/ADR-0007-escopo-de-path-vazio-na-selecao-de-contexto.md) | architecture | Active | Platform Engineering | OnDemand | 1155 |
| [ADR-0008 — Semântica do Conselho e o fim da recursão de crítico](decisions/ADR-0008-semantica-do-conselho-e-o-fim-da-recursao-de-critico.md) | planning | Active | Platform Engineering | OnDemand | 1562 |
| [ADR-0009 — ProjectGraphProjection: um grafo que é projeção, nunca fonte da verdade](decisions/ADR-0009-project-graph-projection.md) | architecture | Active | Platform Engineering | OnDemand | 1074 |

#### entrypoint

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Poseidon](../README.md) | * | Active | Platform Engineering | OnDemand | 807 |

#### governance

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Núcleo de governança do Poseidon](../governance/core.md) | * | Active | Platform Governance | Always | 1361 |

#### product-standard

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Definition of Done do produto entregue](product/definition-of-done.md) | * | Active | Quality Engineering | Bundle | 1193 |
| [Baseline técnico do produto entregue](product/baseline.md) | * | Active | Platform Engineering | Bundle | 1803 |
| [Artefatos fornecidos pelo usuário — protótipo, documento e código existente](product/provided-artifacts.md) | * | Active | Platform Engineering | OnDemand | 1301 |
| [Integração full-stack do produto entregue](product/full-stack-integration.md) | * | Active | Platform Engineering | OnDemand | 744 |
| [Segurança e operabilidade do produto entregue](product/security-and-operability.md) | * | Active | Product Security | OnDemand | 1077 |
| [QA do produto entregue](product/qa-standards.md) | * | Active | Platform Engineering | OnDemand | 1452 |
| [Backend do produto entregue — arquitetura, .NET/C# e API](product/backend-standards.md) | * | Active | Platform Engineering | OnDemand | 1476 |
| [Dados e banco do produto entregue](product/data-standards.md) | * | Active | Platform Engineering | OnDemand | 756 |
| [Frontend do produto entregue](product/frontend-standards.md) | * | Active | Platform Engineering | OnDemand | 1008 |
| [Autenticação e sessão do produto entregue](product/authentication-standards.md) | * | Active | Platform Engineering | OnDemand | 977 |
| [Dados e banco do produto entregue — Oracle](product/oracle-data-standards.md) | * | Active | Platform Engineering | OnDemand | 1539 |
| [Checklist genérico de autoauditoria de IA](product/checklist-auto-auditoria-ia.md) | validate | Active | Platform Engineering | OnDemand | 7218 |

#### rule

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Autoridade e publicação](../governance/rules/authority.md) | * | Active | Platform Governance | Bundle | 267 |
| [Restrições da Bruna](../governance/rules/chief-constraints.md) | * | Active | Platform Governance | Bundle | 315 |
| [Revisão de código](../governance/rules/code-review.md) | * | Active | Quality Engineering | Bundle | 275 |
| [Segredos](../governance/rules/secrets.md) | * | Active | Product Security | Bundle | 257 |
| [Segurança](../governance/rules/security.md) | * | Active | Product Security | Bundle | 305 |
| [Coordenação](../governance/rules/coordination.md) | * | Active | Platform Governance | Bundle | 672 |
| [Documentação](../governance/rules/documentation.md) | * | Active | Platform Governance | Bundle | 671 |
| [Git e integração](../governance/rules/git.md) | * | Active | Platform Engineering | Bundle | 293 |
| [Testes e gates](../governance/rules/testing.md) | * | Active | Quality Engineering | Bundle | 287 |
| [Capacidade e cotas](../governance/rules/capacity.md) | * | Active | Operations | Bundle | 227 |
| [Custos](../governance/rules/cost.md) | * | Active | Operations | Bundle | 234 |

#### runbook

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Runbook de incidente](backend/runbooks/incident.md) | sustentation | Active | Operations | OnDemand | 228 |
| [Runbook de recuperação](backend/runbooks/recovery.md) | sustentation | Active | Operations | OnDemand | 2570 |
| [Runbook do piloto real](backend/runbooks/piloto-real.md) | homologation | Active | Operations | OnDemand | 1280 |

#### security

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Segurança de segredos](security/secrets.md) | * | Active | Product Security | Bundle | 210 |
| [Threat model](security/threat-model.md) | * | Active | Product Security | Bundle | 321 |
| [Isolamento](security/isolation.md) | * | Active | Product Security | Bundle | 234 |

#### workflow

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Máquina de estados do card](architecture/workflows/card-state-machine.md) | * | Active | Platform Governance | Bundle | 278 |
| [Workflow padrão de nove fases](architecture/workflows/standard-workflow.md) | * | Active | Platform Governance | Bundle | 2820 |
| [Intake de demandas](architecture/workflows/intake.md) | triage | Active | Product Security | Bundle | 242 |
| [Paralelismo](architecture/workflows/parallelism.md) | planning, development | Active | Platform Engineering | Bundle | 242 |

### Adapter

#### adapter

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Instruções do repositório para Codex e agentes compatíveis](../AGENTS.md) | * | Active | Platform Governance | Always | 314 |
| [Instruções do repositório para Claude Code](../CLAUDE.md) | * | Active | Platform Governance | Always | 311 |

### Generated

#### index

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Índice da documentação do Poseidon](INDEX.md) | * | Active | Platform Governance | Always | generated |
