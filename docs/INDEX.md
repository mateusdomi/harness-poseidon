<!-- GENERATED FILE — DO NOT EDIT. source=governance/manifest.yaml version=1.0.0 checksum=sha256:d6399899dac4dc18483884c79047c6462e3c7de4bd4c75636941a251261f41d4 -->
# Poseidon documentation index

Generated from `governance/manifest.yaml`. Regenerate with `tools/backend/generate-governance.sh`.

## By authority

- `Adapter`: 2
- `Canonical`: 23
- `Evidence`: 152
- `Generated`: 1
- `Historical`: 8
- `Operational`: 14
- `Reference`: 25

## By domain

- `adapter`: 2
- `architecture`: 4
- `backend`: 1
- `contract`: 1
- `decision`: 21
- `entrypoint`: 1
- `evidence`: 152
- `execution`: 9
- `frontend`: 5
- `governance`: 1
- `index`: 1
- `operations`: 6
- `prompt`: 3
- `reference`: 1
- `research`: 4
- `rule`: 6
- `runbook`: 1
- `security`: 3
- `state`: 2
- `testing`: 1

## By phase

- `*`: 208
- `incident-response`: 1
- `p0`: 1
- `p1`: 1
- `p2`: 1
- `release-candidate`: 4
- `runtime`: 9

## By status

- `Active`: 217
- `Historical`: 7
- `Superseded`: 1

## By owner

- `API Platform`: 1
- `Agent Platform`: 5
- `Architecture`: 18
- `Frontend`: 6
- `Operations`: 6
- `Platform Engineering`: 12
- `Platform Governance`: 14
- `Product Security`: 6
- `Quality Engineering`: 151
- `Research`: 4
- `Technical Writing`: 2

## By load policy

- `Always`: 1
- `Bundle`: 13
- `Entry`: 4
- `Never`: 4
- `OnDemand`: 203

## Documents by authority and domain

### Canonical

#### architecture

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Runtime de governança de agentes](backend/architecture/AGENT_GOVERNANCE_RUNTIME.md) | runtime | Active | Agent Platform | OnDemand | 663 |
| [Mapa de contexto](architecture/CONTEXT_MAP.md) | * | Active | Architecture | Bundle | 120 |
| [Invariantes arquiteturais e de domínio](architecture/INVARIANTS.md) | * | Active | Architecture | Bundle | 272 |
| [Catálogo de módulos](architecture/MODULE_CATALOG.md) | * | Active | Architecture | Bundle | 278 |

#### decision

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [ADR-016 — Executor opcional OMP RPC](decisions/ADR-016-omp-rpc-agent-executor.md) | runtime | Active | Agent Platform | OnDemand | 425 |
| [ADR-017 — Prontidão canônica e estado de configuração fail-closed](decisions/ADR-017-readiness-and-configuration-state.md) | runtime | Active | Platform Governance | OnDemand | 947 |
| [ADR-018 — Dados simulados nunca apresentados como reais](decisions/ADR-018-fail-closed-simulated-data.md) | runtime | Active | Platform Governance | OnDemand | 728 |
| [ADR-019 — Separar confirmação de transporte da resposta real do Chief](decisions/ADR-019-chief-transport-vs-model-response.md) | runtime | Active | Agent Platform | OnDemand | 737 |
| [ADR-021 — Contas de agente por alias e executores reais](decisions/ADR-021-agent-accounts-and-executors.md) | runtime | Active | Agent Platform | OnDemand | 933 |
| [ADR-020 — Auditoria do campo criatividade e binding obrigatório](decisions/ADR-020-creativity-field-audit.md) | runtime | Active | Platform Governance | OnDemand | 566 |

#### execution

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Plano mestre](backend/execution/MASTER_PLAN.md) | * | Active | Platform Engineering | OnDemand | 1512 |
| [Riscos ativos](backend/execution/RISKS.md) | * | Active | Platform Engineering | OnDemand | 987 |

#### governance

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Poseidon — núcleo canônico de governança](../governance/core.md) | * | Active | Platform Governance | Always | 828 |

#### rule

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Regra canônica — coordenação e escopo de agentes](../governance/rules/coordination.md) | * | Active | Agent Platform | Bundle | 311 |
| [Regra canônica — documentação e manifest](../governance/rules/documentation.md) | * | Active | Technical Writing | Bundle | 222 |
| [Regra canônica — Git e preservação de trabalho](../governance/rules/git.md) | * | Active | Platform Engineering | Bundle | 215 |
| [Regra canônica — segredos e redaction](../governance/rules/secrets.md) | * | Active | Product Security | Bundle | 218 |
| [Regra canônica — segurança de execução](../governance/rules/security.md) | * | Active | Product Security | Bundle | 201 |
| [Regra canônica — testes, gates e evidência](../governance/rules/testing.md) | * | Active | Quality Engineering | Bundle | 218 |

#### runbook

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Rotação de segredos](backend/operations/SECRET_ROTATION.md) | incident-response | Active | Product Security | OnDemand | 566 |

#### security

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Threat model — Harness Poseidon backend (release 1.0)](backend/security/THREAT_MODEL.md) | * | Active | Product Security | Bundle | 1469 |
| [Checklist de release 1.0 e DoD global](backend/security/RELEASE_CHECKLIST.md) | * | Active | Product Security | Bundle | 614 |

#### testing

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Estratégia de testes](testing/STRATEGY.md) | * | Active | Quality Engineering | Bundle | 227 |

### Adapter

#### adapter

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Codex and compatible agents repository instructions](../AGENTS.md) | * | Active | Platform Governance | Entry | 270 |
| [Claude Code repository instructions](../CLAUDE.md) | * | Active | Platform Governance | Entry | 267 |

### Generated

#### index

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Poseidon documentation index](INDEX.md) | * | Active | Technical Writing | Entry | generated |

### Operational

#### execution

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Decisões pendentes e suposições](backend/execution/DECISIONS_PENDING.md) | * | Active | Platform Engineering | OnDemand | 588 |
| [Harness Desktop — instalação, operação e desinstalação (modo pessoal)](backend/execution/DESKTOP.md) | * | Active | Platform Engineering | OnDemand | 596 |
| [Ambiente de execução](backend/execution/ENVIRONMENT.md) | * | Active | Platform Engineering | OnDemand | 1721 |
| [Escopo da Fase 0](backend/execution/PHASE_0_SCOPE.md) | * | Active | Platform Engineering | OnDemand | 227 |
| [Progresso e evidências](backend/execution/PROGRESS.md) | * | Active | Platform Engineering | OnDemand | 6416 |
| [Progresso auditável do roadmap — Harness Poseidon backend](backend/execution/ROADMAP_PROGRESS.md) | * | Active | Platform Engineering | OnDemand | 2076 |

#### operations

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Roteiro de homologação da Release Candidate](backend/operations/HOMOLOGATION.md) | release-candidate | Active | Quality Engineering | OnDemand | 1107 |
| [Poseidon Release Candidate — notas de release](backend/release/RELEASE_NOTES.md) | release-candidate | Active | Operations | OnDemand | 674 |
| [Runbook de incidentes](backend/operations/INCIDENT_RUNBOOK.md) | * | Active | Operations | OnDemand | 774 |
| [Instalação do Harness Poseidon](backend/operations/INSTALLATION.md) | * | Active | Operations | OnDemand | 810 |
| [Operação](backend/operations/OPERATIONS.md) | * | Active | Operations | OnDemand | 719 |
| [Configuração local de contas de agente — exemplo](backend/operations/AGENT_ACCOUNTS_EXAMPLE.md) | runtime | Active | Operations | OnDemand | 446 |

#### state

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Estado atual do backend](backend/execution/CURRENT_STATE.md) | * | Active | Platform Engineering | Bundle | 11706 |
| [CURRENT_STATE — Frontend Harness Poseidon](frontend/CURRENT_STATE.md) | * | Active | Frontend | OnDemand | 4623 |

### Reference

#### backend

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Catálogo de referências](backend/product/reference/CATALOG.md) | * | Active | Platform Engineering | OnDemand | 134 |

#### contract

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Reconciliação de contratos](contracts/CONTRACT_RECONCILIATION.md) | * | Active | API Platform | OnDemand | 2289 |

#### decision

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [ADR-001 — Monólito modular e Runner separado](decisions/ADR-001-modular-monolith.md) | * | Active | Architecture | OnDemand | 104 |
| [ADR-002 — .NET 10 com toolchain local reproduzível](decisions/ADR-002-dotnet-toolchain.md) | * | Active | Architecture | OnDemand | 107 |
| [ADR-003 — Persistência dual e autoridade exclusiva do Host](decisions/ADR-003-persistence-authority.md) | * | Active | Architecture | OnDemand | 398 |
| [ADR-004 — Estado relacional, ledger, Inbox e Outbox](decisions/ADR-004-state-audit-messaging.md) | * | Active | Architecture | OnDemand | 491 |
| [ADR-005 — Motor durável específico atrás de interface](decisions/ADR-005-durable-engine.md) | * | Active | Architecture | OnDemand | 528 |
| [ADR-006 — Chefe como ator lógico persistido](decisions/ADR-006-chief-logical-actor.md) | * | Active | Architecture | OnDemand | 107 |
| [ADR-007 — IPC loopback autenticado e filas internas](decisions/ADR-007-local-ipc-and-queues.md) | * | Active | Architecture | OnDemand | 317 |
| [ADR-008 — Estratégia de executores de agente](decisions/ADR-008-agent-executors.md) | * | Active | Architecture | OnDemand | 332 |
| [ADR-009 — Ferramentas tipadas e MCP estável](decisions/ADR-009-tools-and-mcp.md) | * | Active | Architecture | OnDemand | 101 |
| [ADR-010 — Estratégia Git do produto e do repositório oficial](decisions/ADR-010-git-strategy.md) | * | Active | Architecture | OnDemand | 305 |
| [ADR-011 — Sandbox Docker no macOS](decisions/ADR-011-sandbox-docker.md) | * | Active | Architecture | OnDemand | 323 |
| [ADR-012 — Segredos no macOS Keychain e proxy de credenciais](decisions/ADR-012-secret-storage.md) | * | Active | Architecture | OnDemand | 93 |
| [ADR-013 — Contrato REST/OpenAPI e SignalR](decisions/ADR-013-api-and-realtime-contract.md) | * | Active | Architecture | OnDemand | 329 |
| [ADR-014 — ArchUnitNET com verificação estrutural complementar](decisions/ADR-014-architecture-tests.md) | * | Active | Architecture | OnDemand | 191 |
| [ADR-015 — Arquitetura documental de governança de agentes](decisions/ADR-015-agent-governance-document-architecture.md) | * | Active | Architecture | OnDemand | 405 |

#### entrypoint

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Harness Poseidon](../README.md) | * | Active | Platform Engineering | Entry | 119 |

#### execution

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Golden path — handoff backend para frontend](backend/execution/GOLDEN_PATH_HANDOFF.md) | runtime | Active | Platform Governance | OnDemand | 2060 |

#### frontend

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [DECISIONS — Frontend Harness Poseidon](frontend/DECISIONS.md) | * | Active | Frontend | OnDemand | 21276 |
| [HANDOFF — Camada de API do Frontend](frontend/HANDOFF_API.md) | * | Active | Frontend | OnDemand | 9962 |
| [PROGRESS — Frontend Harness Poseidon](frontend/PROGRESS.md) | * | Active | Frontend | OnDemand | 686 |
| [Auditoria e relatório — Refinamento funcional FR-1 a FR-5](frontend/REFINEMENT_AUDIT.md) | * | Active | Frontend | OnDemand | 1272 |
| [SCREENS — Inventário de telas e auditoria de estados (FR-5)](frontend/SCREENS.md) | * | Active | Frontend | OnDemand | 6402 |

#### reference

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Poseidon — Frontend](../frontend/README.md) | * | Active | Platform Governance | OnDemand | 1044 |

### Evidence

#### evidence

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Incidente P1 — first-run da Release Candidate](backend/execution/evidence/RC-FIRST-RUN-P1.md) | release-candidate | Active | Quality Engineering | OnDemand | 1106 |
| [Evidência — Governance Gate P1](backend/execution/evidence/GOVERNANCE-GATE-P1.md) | p1 | Active | Platform Governance | OnDemand | 875 |
| [Evidência — Governance Gate P2](backend/execution/evidence/GOVERNANCE-GATE-P2.md) | p2 | Active | Platform Governance | OnDemand | 333 |
| [Relatório da Release Candidate](backend/release/RELEASE_REPORT.md) | release-candidate | Active | Operations | OnDemand | 1475 |
| [Evidência — Governance Gate P0](backend/execution/evidence/GOVERNANCE-GATE-P0.md) | p0 | Active | Platform Governance | OnDemand | 666 |
| [Evidência F0 PoC-1 — SQLite WAL e dispatcher único](backend/execution/evidence/F0-POC-1.md) | * | Active | Quality Engineering | OnDemand | 335 |
| [Evidência F0 PoC-2 — kill -9 e retomada durável](backend/execution/evidence/F0-POC-2.md) | * | Active | Quality Engineering | OnDemand | 405 |
| [Evidência F0 PoC-3 — lease e fencing token](backend/execution/evidence/F0-POC-3.md) | * | Active | Quality Engineering | OnDemand | 321 |
| [Evidência F0 PoC-4 — subprocesso Codex CLI, heartbeat e retomada](backend/execution/evidence/F0-POC-4.md) | * | Active | Quality Engineering | OnDemand | 591 |
| [Evidência F0 PoC-5 — branches, worktrees e claims em fixtures](backend/execution/evidence/F0-POC-5.md) | * | Active | Quality Engineering | OnDemand | 503 |
| [Evidência F0 PoC-6 — sandbox Docker, limites, proxy e cleanup](backend/execution/evidence/F0-POC-6.md) | * | Active | Quality Engineering | OnDemand | 746 |
| [Evidência F0 PoC-7 — SignalR sequenciado e re-sync snapshot+delta](backend/execution/evidence/F0-POC-7.md) | * | Active | Quality Engineering | OnDemand | 618 |
| [Evidência F0 PoC-8 — PostgreSQL dual, SKIP LOCKED e fencing](backend/execution/evidence/F0-POC-8.md) | * | Active | Quality Engineering | OnDemand | 755 |
| [Evidência F0 PoC-9 — IPC Host–Runner autenticado e idempotente](backend/execution/evidence/F0-POC-9.md) | * | Active | Quality Engineering | OnDemand | 587 |
| [Evidência F1 — aprovações documentais transacionais](backend/execution/evidence/F1-DOCUMENT-APPROVALS.md) | * | Active | Quality Engineering | OnDemand | 443 |
| [Evidência F1 — contrato de documentos versionados](backend/execution/evidence/F1-DOCUMENT-CONTRACT.md) | * | Active | Quality Engineering | OnDemand | 305 |
| [Evidência F1 — metadados e lifecycle documental](backend/execution/evidence/F1-DOCUMENT-LIFECYCLE.md) | * | Active | Quality Engineering | OnDemand | 393 |
| [Evidência F1 — schema dual-provider de documentos](backend/execution/evidence/F1-DOCUMENT-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 314 |
| [Evidência F1 — criação e leitura transacional de documentos](backend/execution/evidence/F1-DOCUMENT-STORE-CREATION.md) | * | Active | Quality Engineering | OnDemand | 384 |
| [Evidência F1 — versionamento transacional de documentos](backend/execution/evidence/F1-DOCUMENT-VERSIONING.md) | * | Active | Quality Engineering | OnDemand | 356 |
| [Evidência F1 — borda comum do motor durável](backend/execution/evidence/F1-DURABLE-ENGINE-BOUNDARY.md) | * | Active | Quality Engineering | OnDemand | 189 |
| [Evidência F1 — contrato do motor durável](backend/execution/evidence/F1-DURABLE-ENGINE-CONTRACT.md) | * | Active | Quality Engineering | OnDemand | 282 |
| [Evidência F1 — motor durável PostgreSQL](backend/execution/evidence/F1-DURABLE-ENGINE-POSTGRES.md) | * | Active | Quality Engineering | OnDemand | 446 |
| [Evidência F1 — schema do motor durável](backend/execution/evidence/F1-DURABLE-ENGINE-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 279 |
| [Evidência F1 — motor durável SQLite](backend/execution/evidence/F1-DURABLE-ENGINE-SQLITE.md) | * | Active | Quality Engineering | OnDemand | 336 |
| [Evidência F1 — schema de fundação dual-provider](backend/execution/evidence/F1-FOUNDATION-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 341 |
| [Encerramento formal da Fase 1 — GNG-2](backend/execution/evidence/F1-GNG2-CLOSURE.md) | * | Active | Quality Engineering | OnDemand | 330 |
| [Evidência F1 — recuperação abrupta GNG-2](backend/execution/evidence/F1-GNG2-RECOVERY.md) | * | Active | Quality Engineering | OnDemand | 495 |
| [F1-WRK-1d.3b — Host pessoal com realtime persistido](backend/execution/evidence/F1-HOST-PERSISTED-REALTIME.md) | * | Active | Quality Engineering | OnDemand | 482 |
| [Evidência F1 — contrato e schema de dispatch da Outbox](backend/execution/evidence/F1-OUTBOX-DISPATCH-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 362 |
| [Evidência F1 — worker durável da Outbox](backend/execution/evidence/F1-OUTBOX-DISPATCHER-WORKER.md) | * | Active | Quality Engineering | OnDemand | 419 |
| [Evidência F1 — stores duráveis da Outbox](backend/execution/evidence/F1-OUTBOX-STORES.md) | * | Active | Quality Engineering | OnDemand | 446 |
| [Evidência F1 — contrato e schema durável de realtime](backend/execution/evidence/F1-REALTIME-EVENT-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 359 |
| [Evidência F1 — stores sequenciados de realtime](backend/execution/evidence/F1-REALTIME-EVENT-STORES.md) | * | Active | Quality Engineering | OnDemand | 464 |
| [Evidência F1 — sink persistido Outbox→realtime](backend/execution/evidence/F1-REALTIME-OUTBOX-SINK.md) | * | Active | Quality Engineering | OnDemand | 318 |
| [Evidência F1 — IPC Runner persistido pela autoridade do Host](backend/execution/evidence/F1-RUNNER-IPC-PERSISTENCE.md) | * | Active | Quality Engineering | OnDemand | 458 |
| [Evidência F1 — fundação transacional Inbox/Outbox/ledger](backend/execution/evidence/F1-TRANSACTIONAL-FOUNDATION.md) | * | Active | Quality Engineering | OnDemand | 348 |
| [F1-WRK-2 — Watchdog e reconciliador durável](backend/execution/evidence/F1-WATCHDOG-RECONCILIATION.md) | * | Active | Quality Engineering | OnDemand | 518 |
| [Evidência F1 — contrato da cadeia de trabalho](backend/execution/evidence/F1-WORK-CHAIN-CONTRACT.md) | * | Active | Quality Engineering | OnDemand | 344 |
| [Evidência F1 — correção imutável e reidratação da cadeia](backend/execution/evidence/F1-WORK-CHAIN-HISTORY.md) | * | Active | Quality Engineering | OnDemand | 522 |
| [Evidência F1 — mutações transacionais da cadeia](backend/execution/evidence/F1-WORK-CHAIN-MUTATIONS.md) | * | Active | Quality Engineering | OnDemand | 479 |
| [Evidência F1 — schema da cadeia de trabalho](backend/execution/evidence/F1-WORK-CHAIN-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 438 |
| [Evidência F1 — criação transacional da cadeia](backend/execution/evidence/F1-WORK-CHAIN-STORE.md) | * | Active | Quality Engineering | OnDemand | 402 |
| [Evidência F1 — contrato de workflow, gates e progresso](backend/execution/evidence/F1-WORKFLOW-CONTRACT.md) | * | Active | Quality Engineering | OnDemand | 423 |
| [Evidência F1 — inicialização transacional de WorkflowRun](backend/execution/evidence/F1-WORKFLOW-RUN-CREATION.md) | * | Active | Quality Engineering | OnDemand | 386 |
| [Evidência F1 — lifecycle transacional de WorkflowRun](backend/execution/evidence/F1-WORKFLOW-RUN-LIFECYCLE.md) | * | Active | Quality Engineering | OnDemand | 498 |
| [Evidência F1 — objetivos, gates, fases e progresso de WorkflowRun](backend/execution/evidence/F1-WORKFLOW-RUN-PROGRESS.md) | * | Active | Quality Engineering | OnDemand | 632 |
| [Evidência F1 — schema dual-provider de workflows](backend/execution/evidence/F1-WORKFLOW-SCHEMA.md) | * | Active | Quality Engineering | OnDemand | 375 |
| [Evidência F1 — criação/publicação transacional de workflow](backend/execution/evidence/F1-WORKFLOW-STORE.md) | * | Active | Quality Engineering | OnDemand | 383 |
| [Evidência F10-4 — paridade PostgreSQL do quadro, aprovações, catálogo de workflows e cockpit](backend/execution/evidence/F10-BOARD-WORKFLOW-PARITY.md) | * | Active | Quality Engineering | OnDemand | 371 |
| [Evidência F10-2 — paridade PostgreSQL dos catálogos semeados](backend/execution/evidence/F10-CATALOG-PARITY.md) | * | Active | Quality Engineering | OnDemand | 342 |
| [Evidência F10-3 — paridade PostgreSQL de conversas e pipeline do Chief](backend/execution/evidence/F10-CONVERSATION-CHIEF-PARITY.md) | * | Active | Quality Engineering | OnDemand | 431 |
| [Evidência F10-1 — paridade PostgreSQL do núcleo identidade/org/projeto/auditoria](backend/execution/evidence/F10-IDENTITY-CORE-PARITY.md) | * | Active | Quality Engineering | OnDemand | 440 |
| [Evidência F10-6 — modo servidor multiusuário, rate limit e carga de 30 usuários](backend/execution/evidence/F10-MULTIUSER.md) | * | Active | Quality Engineering | OnDemand | 581 |
| [Evidência F10-8 — maquinaria OIDC completa (falta apenas o Entra ID real)](backend/execution/evidence/F10-OIDC.md) | * | Active | Quality Engineering | OnDemand | 513 |
| [Evidência F10-7 — RBAC/ABAC multiusuário (admin/member)](backend/execution/evidence/F10-RBAC.md) | * | Active | Quality Engineering | OnDemand | 465 |
| [Evidência F10-5 — paridade PostgreSQL completa e Host em modo servidor](backend/execution/evidence/F10-SERVER-MODE.md) | * | Active | Quality Engineering | OnDemand | 422 |
| [Evidência F11-1 — hardening de release: upgrade, threat model, SBOM, fixture PG](backend/execution/evidence/F11-HARDENING.md) | * | Active | Quality Engineering | OnDemand | 378 |
| [Evidência F11-6 — instalação, operação e resposta a incidentes](backend/execution/evidence/F11-OPERATIONS.md) | * | Active | Quality Engineering | OnDemand | 294 |
| [Evidência F11-5 — matriz formal de resiliência](backend/execution/evidence/F11-RESILIENCE-MATRIX.md) | * | Active | Quality Engineering | OnDemand | 393 |
| [Evidência F11-7 — SAST dedicado e hardening do storage de anexos](backend/execution/evidence/F11-SAST.md) | * | Active | Quality Engineering | OnDemand | 437 |
| [Evidência F11-3 — scanning de segredos (gate contínuo + entrypoint operacional)](backend/execution/evidence/F11-SECRET-SCANNING.md) | * | Active | Quality Engineering | OnDemand | 303 |
| [Evidência F11-2 — hardening de borda: security headers, cookie e CORS default-deny](backend/execution/evidence/F11-SECURITY-HEADERS.md) | * | Active | Quality Engineering | OnDemand | 414 |
| [F11 — Migração de dados SQLite → PostgreSQL](backend/execution/evidence/F11-SQLITE-TO-POSTGRES-MIGRATION.md) | * | Active | Quality Engineering | OnDemand | 1154 |
| [Evidência F2-ORCH-1a — catálogo e instâncias de agentes](backend/execution/evidence/F2-AGENT-CATALOG.md) | * | Active | Quality Engineering | OnDemand | 309 |
| [Evidência F2-DOGFOOD-1a — execução estruturada de agentes](backend/execution/evidence/F2-AGENT-EXECUTION.md) | * | Active | Quality Engineering | OnDemand | 428 |
| [Evidência F2-APP-1 — central unificada de aprovações](backend/execution/evidence/F2-APPROVAL-CENTER.md) | * | Active | Quality Engineering | OnDemand | 246 |
| [Evidência F2-DOGFOOD-1d.2a — claims de escopo e catálogo de workspace duráveis](backend/execution/evidence/F2-ATTEMPT-WORKSPACE-CLAIMS.md) | * | Active | Quality Engineering | OnDemand | 650 |
| [Evidência F2-ORCH-1b — comandos do Chief](backend/execution/evidence/F2-CHIEF-COMMANDS.md) | * | Active | Quality Engineering | OnDemand | 324 |
| [Evidência F2-DOGFOOD-2a — materialização de demandas do Chief](backend/execution/evidence/F2-CHIEF-DEMAND-MATERIALIZATION.md) | * | Active | Quality Engineering | OnDemand | 381 |
| [Evidência F2-DOGFOOD-1b — mailbox e lease do Chief](backend/execution/evidence/F2-CHIEF-TURN-PIPELINE.md) | * | Active | Quality Engineering | OnDemand | 371 |
| [Evidência F2-DOGFOOD-1c — reconciliação de turnos do Chief](backend/execution/evidence/F2-CHIEF-TURN-RECONCILIATION.md) | * | Active | Quality Engineering | OnDemand | 281 |
| [F2-CPK-1 — Cockpit e StatusDigest determinístico](backend/execution/evidence/F2-COCKPIT-DIGEST.md) | * | Active | Quality Engineering | OnDemand | 395 |
| [F2-CHAT-1 — Conversas, mensagens e streaming durável](backend/execution/evidence/F2-CONVERSATIONS-CHAT.md) | * | Active | Quality Engineering | OnDemand | 480 |
| [Evidência F2-DOGFOOD-1e.1 — composição real Docker + protocolo Codex](backend/execution/evidence/F2-DOCKER-ATTEMPT-COMPOSITION.md) | * | Active | Quality Engineering | OnDemand | 377 |
| [Evidência F2-DOC-1a — catálogo e versões documentais](backend/execution/evidence/F2-DOCUMENT-CATALOG.md) | * | Active | Quality Engineering | OnDemand | 248 |
| [Evidência F2-DOC-1b — lifecycle e aprovação documental](backend/execution/evidence/F2-DOCUMENT-LIFECYCLE.md) | * | Active | Quality Engineering | OnDemand | 273 |
| [Evidência F2-DOGFOOD-2b — pipeline dogfood ponta a ponta](backend/execution/evidence/F2-DOGFOOD-PIPELINE.md) | * | Active | Quality Engineering | OnDemand | 414 |
| [Evidência F2-FE-1 — integração reproduzível do frontend](backend/execution/evidence/F2-FRONTEND-INTEGRATION.md) | * | Active | Quality Engineering | OnDemand | 420 |
| [Evidência F2-GOV-1 — governança e auditoria](backend/execution/evidence/F2-GOVERNANCE-AUDIT.md) | * | Active | Quality Engineering | OnDemand | 318 |
| [Evidência F2-DOGFOOD-1d.2b — composição da tentativa externa](backend/execution/evidence/F2-ISOLATED-ATTEMPT-ORCHESTRATION.md) | * | Active | Quality Engineering | OnDemand | 620 |
| [Evidência F2-DOGFOOD-1d.2c — recovery da composição por estágio](backend/execution/evidence/F2-ISOLATED-ATTEMPT-RECOVERY.md) | * | Active | Quality Engineering | OnDemand | 476 |
| [Evidência F2-DOGFOOD-1d.1 — sessão Codex isolada](backend/execution/evidence/F2-ISOLATED-CODEX-SESSION.md) | * | Active | Quality Engineering | OnDemand | 404 |
| [Evidência F2-DOGFOOD-1e.2 — fiação DI e API tipada da execução isolada](backend/execution/evidence/F2-ISOLATED-EXECUTION-API.md) | * | Active | Quality Engineering | OnDemand | 398 |
| [Evidência F2-LIC-1 — licenças e entitlements](backend/execution/evidence/F2-LICENSING.md) | * | Active | Quality Engineering | OnDemand | 281 |
| [Evidência F2-OPS-1 — backup, restore e diagnóstico local](backend/execution/evidence/F2-LOCAL-OPERATIONS.md) | * | Active | Quality Engineering | OnDemand | 281 |
| [F2-ID-1 — Perfil local](backend/execution/evidence/F2-LOCAL-PROFILE.md) | * | Active | Quality Engineering | OnDemand | 317 |
| [Evidência F2-NOTIF-1 — notificações e settings](backend/execution/evidence/F2-NOTIFICATIONS-SETTINGS.md) | * | Active | Quality Engineering | OnDemand | 348 |
| [F2-ORG-1 — Organizações pessoais](backend/execution/evidence/F2-ORGANIZATIONS.md) | * | Active | Quality Engineering | OnDemand | 377 |
| [Evidência — adoção de sessão no modo pessoal (desbloqueio da homologação GNG-3)](backend/execution/evidence/F2-PERSONAL-SESSION-ADOPTION.md) | * | Active | Quality Engineering | OnDemand | 431 |
| [F2-PRJ-1 — Projetos pessoais](backend/execution/evidence/F2-PROJECTS.md) | * | Active | Quality Engineering | OnDemand | 419 |
| [Evidência F2-PROT-1 — prototipação e referências visuais](backend/execution/evidence/F2-PROTOTYPING.md) | * | Active | Quality Engineering | OnDemand | 303 |
| [Evidência F2-PROV-1 — providers, roteamento e budgets](backend/execution/evidence/F2-PROVIDERS-ROUTING-BUDGETS.md) | * | Active | Quality Engineering | OnDemand | 278 |
| [Evidência F2-DOGFOOD-3a — smoke com agente de IA real no pipeline isolado](backend/execution/evidence/F2-REAL-AGENT-SMOKE.md) | * | Active | Quality Engineering | OnDemand | 426 |
| [Evidência F2-RUN-1 — run targets .NET e Node](backend/execution/evidence/F2-RUN-TARGETS.md) | * | Active | Quality Engineering | OnDemand | 396 |
| [Evidência F2-PO-1 — análise de solicitação](backend/execution/evidence/F2-SOLICITATION-ANALYSIS.md) | * | Active | Quality Engineering | OnDemand | 269 |
| [Evidência F2-TOOL-1 — catálogo e política de ferramentas](backend/execution/evidence/F2-TOOL-CATALOG-POLICY.md) | * | Active | Quality Engineering | OnDemand | 305 |
| [F2-WORK-1b — comandos e lifecycle do quadro](backend/execution/evidence/F2-WORK-BOARD-COMMANDS.md) | * | Active | Quality Engineering | OnDemand | 434 |
| [F2-WORK-1a — Cadeia e quadro: criação e leitura](backend/execution/evidence/F2-WORK-BOARD-READ-CREATE.md) | * | Active | Quality Engineering | OnDemand | 501 |
| [F2-WF-1a — catálogo, vínculo e run de workflow](backend/execution/evidence/F2-WORKFLOW-CATALOG.md) | * | Active | Quality Engineering | OnDemand | 398 |
| [Evidência F2-WF-1b — comandos e lifecycle de workflow](backend/execution/evidence/F2-WORKFLOW-COMMANDS.md) | * | Active | Quality Engineering | OnDemand | 417 |
| [Evidência F3-1 — templates canônicos de workflow e variantes](backend/execution/evidence/F3-CANONICAL-WORKFLOW-TEMPLATES.md) | * | Active | Quality Engineering | OnDemand | 419 |
| [Evidência F3-3 — verificação de consistência em camadas](backend/execution/evidence/F3-CONSISTENCY-VERIFICATION.md) | * | Active | Quality Engineering | OnDemand | 447 |
| [Evidência F3-2 — bloqueios invioláveis do modo autônomo](backend/execution/evidence/F3-INVIOLABLE-ACTION-GUARD.md) | * | Active | Quality Engineering | OnDemand | 516 |
| [Evidência F3-4 — workflow completo em modo semiautônomo (fechamento da Fase 3)](backend/execution/evidence/F3-SEMIAUTONOMOUS-WORKFLOW.md) | * | Active | Quality Engineering | OnDemand | 458 |
| [Evidência F4-1 — segurança de upload do PO Assistant](backend/execution/evidence/F4-ATTACHMENT-UPLOAD-SECURITY.md) | * | Active | Quality Engineering | OnDemand | 483 |
| [Evidência F5-1 — upload de imagens e ZIP para referências visuais](backend/execution/evidence/F5-REFERENCE-ASSET-UPLOAD.md) | * | Active | Quality Engineering | OnDemand | 255 |
| [Evidência F6-1 — três stacks reais com health e camadas de detecção](backend/execution/evidence/F6-RUN-PROJECT-STACKS.md) | * | Active | Quality Engineering | OnDemand | 1337 |
| [Evidência F7-1 — Launcher real e publish desktop self-contained](backend/execution/evidence/F7-DESKTOP-LAUNCHER.md) | * | Active | Quality Engineering | OnDemand | 434 |
| [Evidência F7-2 — ciclo de vida desktop seguro](backend/execution/evidence/F7-DESKTOP-LIFECYCLE.md) | * | Active | Quality Engineering | OnDemand | 504 |
| [Evidência F8-1 — licenciamento por documento assinado Ed25519](backend/execution/evidence/F8-SIGNED-LICENSING.md) | * | Active | Quality Engineering | OnDemand | 491 |
| [Evidência F9-1 — gateway de canais com linking e dedupe de reentrega](backend/execution/evidence/F9-CHANNEL-GATEWAY.md) | * | Active | Quality Engineering | OnDemand | 429 |
| [Evidência F9-4 — adaptador Teams sobre o gateway de canais](backend/execution/evidence/F9-TEAMS-ADAPTER.md) | * | Active | Quality Engineering | OnDemand | 480 |
| [Evidência F9-2 — adaptador Telegram sobre o gateway de canais](backend/execution/evidence/F9-TELEGRAM-ADAPTER.md) | * | Active | Quality Engineering | OnDemand | 497 |
| [Evidência F9-3 — smoke real do canal Telegram (@SystemPoseidon_bot)](backend/execution/evidence/F9-TELEGRAM-REAL-SMOKE.md) | * | Active | Quality Engineering | OnDemand | 393 |
| [Governance — baseline e matriz de delta pré-G0](backend/execution/evidence/GOVERNANCE-BASELINE-DELTA.md) | * | Active | Quality Engineering | OnDemand | 1635 |
| [V3 — defaults operacionais das definições de agentes](backend/execution/evidence/V3-AGENT-DEFINITION-DEFAULTS.md) | * | Active | Quality Engineering | OnDemand | 218 |
| [V3 — histórico consultável das definições de agentes](backend/execution/evidence/V3-AGENT-DEFINITION-HISTORY.md) | * | Active | Quality Engineering | OnDemand | 204 |
| [V3 — CRUD e lifecycle de definições de agentes](backend/execution/evidence/V3-AGENT-DEFINITION-LIFECYCLE.md) | * | Active | Quality Engineering | OnDemand | 164 |
| [V3 — seleção persistida de conta, modelo, effort e fallback por agente](backend/execution/evidence/V3-AGENT-MODEL-SELECTION.md) | * | Active | Quality Engineering | OnDemand | 158 |
| [Refinamento v3 — read-model de organograma de agentes](backend/execution/evidence/V3-AGENT-ORG-CHART.md) | * | Active | Quality Engineering | OnDemand | 395 |
| [V3 — severidade tipada dos eventos de tentativa](backend/execution/evidence/V3-ATTEMPT-EVENT-SEVERITY.md) | * | Active | Quality Engineering | OnDemand | 165 |
| [V3 — routing e override por invocação do Chief](backend/execution/evidence/V3-CHIEF-INVOCATION-ROUTING.md) | * | Active | Quality Engineering | OnDemand | 353 |
| [Refinamento v3 — edição manual como versão imutável](backend/execution/evidence/V3-DOCUMENT-MANUAL-VERSION.md) | * | Active | Quality Engineering | OnDemand | 425 |
| [V3 — catálogo tipado de esforço por modelo](backend/execution/evidence/V3-MODEL-EFFORT-MAPPINGS.md) | * | Active | Quality Engineering | OnDemand | 221 |
| [Evidência v3 — administração segura de conta de provider](backend/execution/evidence/V3-PROVIDER-ACCOUNT-MUTATION.md) | * | Active | Quality Engineering | OnDemand | 521 |
| [V3 — administração tipada de modelos de provider](backend/execution/evidence/V3-PROVIDER-MODEL-ADMIN.md) | * | Active | Quality Engineering | OnDemand | 263 |
| [Refinamento v3 — arquivamento de tarefas](backend/execution/evidence/V3-TASK-ARCHIVING.md) | * | Active | Quality Engineering | OnDemand | 438 |
| [V3 — fases persistidas no quadro](backend/execution/evidence/V3-WORK-BOARD-PHASES.md) | * | Active | Quality Engineering | OnDemand | 338 |
| [V3 — ciclo de rascunho e publicação de workflows](backend/execution/evidence/V3-WORKFLOW-DRAFT-LIFECYCLE.md) | * | Active | Quality Engineering | OnDemand | 650 |
| [Resolução de autenticação dos perfis isolados](backend/execution/evidence/AUTH-RESOLUTION.md) | * | Active | Quality Engineering | OnDemand | 1317 |
| [Auto-key versionado de agent_key](backend/execution/evidence/AUTO-KEY-AGENT-DEFINITIONS.md) | * | Active | Quality Engineering | OnDemand | 594 |
| [Backend write-scope e claims granulares](backend/execution/evidence/BACKEND-SCOPE-GRANULAR-CLAIMS.md) | * | Active | Quality Engineering | OnDemand | 560 |
| [CA-1/CA-2 — papel provider-agnostic e registro de contas](backend/execution/evidence/CA-1-2-AGENT-ACCOUNTS.md) | * | Active | Quality Engineering | OnDemand | 845 |
| [CA-3 — isolamento por conta de agente](backend/execution/evidence/CA-3-ACCOUNT-ISOLATION.md) | * | Active | Quality Engineering | OnDemand | 929 |
| [CA-4 — adapters reais de executores externos](backend/execution/evidence/CA-4-EXTERNAL-EXECUTORS.md) | * | Active | Quality Engineering | OnDemand | 1251 |
| [CA-5 — bootstrap governado de agentes](backend/execution/evidence/CA-5-AGENT-RUN-BOOTSTRAP.md) | * | Active | Quality Engineering | OnDemand | 1637 |
| [GP-A — prontidão canônica do golden path](backend/execution/evidence/GP-A-READINESS-CONTRACT.md) | * | Active | Quality Engineering | OnDemand | 819 |
| [GP-B — instalação vazia sem catálogo simulado](backend/execution/evidence/GP-B-FAIL-CLOSED-CATALOG.md) | * | Active | Quality Engineering | OnDemand | 867 |
| [GP-C1 — executor simulado fora do pacote normal](backend/execution/evidence/GP-C1-NO-SIMULATED-CHIEF-REPLY.md) | * | Active | Quality Engineering | OnDemand | 617 |
| [GP-C2 — turno bloqueado tipado](backend/execution/evidence/GP-C2-BLOCKED-TURN-CONTRACT.md) | * | Active | Quality Engineering | OnDemand | 884 |
| [GP-C3 — eventos do ciclo de vida do turno](backend/execution/evidence/GP-C3-TURN-LIFECYCLE-EVENTS.md) | * | Active | Quality Engineering | OnDemand | 707 |
| [GP-C4 — primeira conversa idempotente](backend/execution/evidence/GP-C4-PRIMARY-CONVERSATION.md) | * | Active | Quality Engineering | OnDemand | 519 |
| [GP-C5 — smoke condicional da execução real](backend/execution/evidence/GP-C5-REAL-EXECUTION-SMOKE.md) | * | Active | Quality Engineering | OnDemand | 584 |
| [N3 — Antigravity first-class critic](backend/execution/evidence/N3-ANTIGRAVITY-CRITIC.md) | * | Active | Quality Engineering | OnDemand | 1463 |
| [N4 — scheduler, quotas and fallback](backend/execution/evidence/N4-SCHEDULER-QUOTAS.md) | * | Active | Quality Engineering | OnDemand | 891 |
| [Turno noturno — relatório](backend/execution/evidence/NIGHT-SHIFT-REPORT.md) | * | Active | Quality Engineering | OnDemand | 1559 |
| [Piloto 1A e CA-7 — worker governado e critic independente](backend/execution/evidence/PILOTO-1A-E-CA-7-CRITIC.md) | * | Active | Quality Engineering | OnDemand | 1632 |
| [Piloto 1B — continuação governada de um attempt reprovado](backend/execution/evidence/PILOTO-1B-CONTINUACAO-GOVERNADA.md) | * | Active | Quality Engineering | OnDemand | 1832 |
| [Piloto 2 — No-Go, BLOCKED_EXTERNAL_OAUTH](backend/execution/evidence/PILOTO-2-BLOCKED-EXTERNAL-OAUTH.md) | * | Active | Quality Engineering | OnDemand | 821 |
| [Piloto 2 — fleet concorrente com Antigravity live](backend/execution/evidence/PILOTO-2-FLEET-CONCORRENTE.md) | * | Active | Quality Engineering | OnDemand | 885 |
| [Cota, agendamento e retry inteligente](backend/execution/evidence/QUOTA-RETRY-SCHEDULING.md) | * | Active | Quality Engineering | OnDemand | 599 |

### Historical

#### prompt

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [CONTINUAÇÃO FABLE — BACKEND E PLATAFORMA POSEIDON](../governance/prompts/backend-continuation-v2.0.md) | * | Historical | Platform Governance | Never | 3011 |
| [POSEIDON — CONTINUAÇÃO BACKEND E INTEGRAÇÃO](../governance/prompts/backend-continuation-v3.0.md) | * | Historical | Platform Governance | Never | 1916 |
| [MISSÃO CODEX — CONSTRUIR A PLATAFORMA HARNESS (BACKEND E PLATAFORMA)](../governance/prompts/backend-mission-v1.3.md) | * | Historical | Platform Governance | Never | 8703 |

#### research

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [01 — POSEIDON: INVENTÁRIO, MATRIZ DE CARREGAMENTO E LACUNAS](research/agent-governance/01-POSEIDON-INVENTORY-SUMMARY.md) | * | Historical | Research | OnDemand | 1268 |
| [02 — PESQUISA: HARNESS ENGINEERING E LOOP ENGINEERING](research/agent-governance/02-PESQUISA-PATTERNS-ANTIPATTERNS.md) | * | Historical | Research | OnDemand | 1607 |
| [03 — ANÁLISE: ECC E OH MY PI](research/agent-governance/03-ECC-OMP-ANALISE.md) | * | Historical | Research | OnDemand | 1353 |
| [04 — SÍNTESE E PROPOSTA MACRO (SOMENTE PLANEJAMENTO — NADA FOI IMPLEMENTADO)](research/agent-governance/04-PROPOSTA-MACRO.md) | * | Historical | Research | OnDemand | 2370 |

#### security

| Document | Phase | Status | Owner | Load policy | Tokens |
|---|---|---|---|---|---:|
| [Threat model histórico — substituído](security/THREAT_MODEL.md) | * | Superseded | Product Security | Never | 78 |
