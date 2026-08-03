<!-- GENERATED FILE — DO NOT EDIT. source=governance/manifest.yaml version=2.0.0 checksum=sha256:42eaad17e90935f477837c7b8c3612bfea7e2ef7f3b122c66828e83af14daeb7 -->
# Poseidon documentation index

Generated from `governance/manifest.yaml`. Regenerate with `tools/backend/generate-governance.sh`.

## By authority

- `Adapter`: 2
- `Canonical`: 38
- `Generated`: 1

## By domain

- `adapter`: 2
- `agent`: 1
- `architecture`: 1
- `backend`: 4
- `contract`: 7
- `decision`: 2
- `entrypoint`: 1
- `governance`: 1
- `index`: 1
- `rule`: 11
- `runbook`: 3
- `security`: 3
- `workflow`: 4

## By phase

- `*`: 34
- `architecture`: 2
- `development`: 1
- `homologation`: 1
- `planning`: 1
- `sustentation`: 2
- `triage`: 1

## By status

- `Active`: 41

## By owner

- `Operations`: 7
- `Platform Engineering`: 10
- `Platform Governance`: 14
- `Product Security`: 6
- `Quality Engineering`: 4

## By load policy

- `Always`: 4
- `Bundle`: 31
- `OnDemand`: 6

## Documents by authority and domain

### Canonical

#### agent

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Bruna](agents/bruna.md) | * | Active | Platform Governance | Bundle | 1748 |

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
| [Contrato de card](contracts/card.md) | * | Active | Platform Governance | Bundle | 332 |
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

#### entrypoint

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Poseidon](../README.md) | * | Active | Platform Engineering | OnDemand | 807 |

#### governance

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Núcleo de governança do Poseidon](../governance/core.md) | * | Active | Platform Governance | Always | 1073 |

#### rule

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Autoridade e publicação](../governance/rules/authority.md) | * | Active | Platform Governance | Bundle | 264 |
| [Restrições da Bruna](../governance/rules/chief-constraints.md) | * | Active | Platform Governance | Bundle | 251 |
| [Revisão de código](../governance/rules/code-review.md) | * | Active | Quality Engineering | Bundle | 275 |
| [Segredos](../governance/rules/secrets.md) | * | Active | Product Security | Bundle | 257 |
| [Segurança](../governance/rules/security.md) | * | Active | Product Security | Bundle | 305 |
| [Coordenação](../governance/rules/coordination.md) | * | Active | Platform Governance | Bundle | 336 |
| [Documentação](../governance/rules/documentation.md) | * | Active | Platform Governance | Bundle | 315 |
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
| [Workflow padrão de nove fases](architecture/workflows/standard-workflow.md) | * | Active | Platform Governance | Bundle | 2651 |
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
