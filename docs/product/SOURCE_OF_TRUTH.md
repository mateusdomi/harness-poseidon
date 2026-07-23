# Poseidon — Fonte da Verdade Canônica

> **O que é este documento.** A **fonte da verdade canônica** do Poseidon: o que a plataforma **deve
> fazer** (requisitos), o que **já faz** (implementado + evidência), o que ainda **vai fazer** (planejado),
> e a **análise de pesquisa × arquitetura** que fundamenta as melhorias. Unifica o que antes estava
> fragmentado em missão + `MASTER_PLAN` + `PROGRESS` + `ROADMAP` + backlog + notas de pesquisa.
>
> **Status canônico (decidido pelo usuário em 2026-07-22).** Este é o documento único que o agente Chefe
> **consulta** quando o usuário pergunta sobre funcionalidades, escopo, o que existe ou o que falta.
> **Ainda NÃO revisado/aprovado pelo usuário** — pode conter correções pendentes; o usuário revisa com
> calma e ajusta. O agente **não** começa a trabalhar automaticamente por causa deste arquivo; ele serve
> de **visão garantida** quando consultado.
>
> **Precedência de verdade** (herdada de `governance/core.md`): para **normas**, prevalecem runtime +
> segurança + `core.md` + regras canônicas. Para **fatos operacionais**, prevalecem banco autoritativo +
> Git + evidência executada. Este documento consolida a camada de **funcionalidades/requisitos**;
> execução/evidência fatia-a-fatia continua em `docs/backend/execution/PROGRESS.md` e `ROADMAP_PROGRESS.md`.
> Requisitos originais na **missão v1.3** (`governance/prompts/backend-mission-v1.3.md`, hoje marcada
> *histórica* — ver §8).
>
> **Estado auditável (de `ROADMAP_PROGRESS.md`):** Implementado 100% · Validado ~99,8% · Integrado ~99% ·
> **Homologado 0%** → geral **~79,6%**.

---

## Estrutura

- **PARTE I — Ledger de Funcionalidades** (§0–§8): a fonte da verdade de *features*.
- **PARTE II — Alinhamento Pesquisa × Arquitetura**: o memorando que fundamenta as melhorias `PLAT-xx`.

## Legendas (Parte I)

**Status:** ✅ Feito (evidência executada) · 🟡 Parcial · 🟠 Em andamento (rodada atual) · ⚪ Planejado (aprovado) · 🔵 Proposto (a validar) · 🚫 Bloqueado (externo: credencial/homologação)

**Origem:** `M13` missão v1.3 · `GP` achados do golden path (homologação) · `PZ` pesquisa profunda · `NV` nova área proposta (Delivery/Architecture) · `OPS` operação

**Prioridade:** P1 (bloqueia fluxo principal) · P2 (relevante) · P3 (secundário) · P4 (futuro)

---

## Índice

**Parte I:**
- [0. Plataforma & núcleo (fundação)](#0-plataforma--núcleo-fundação) — quase tudo ✅
- [1. Golden Path — primeiro uso até execução real do Chief](#1-golden-path--primeiro-uso-até-execução-real-do-chief) — 🟠 rodada atual
- [2. Catálogos, agentes e execução real](#2-catálogos-agentes-e-execução-real)
- [3. UX / navegação guiada](#3-ux--navegação-guiada)
- [4. Melhorias de plataforma (pesquisa: contexto, memória, evals)](#4-melhorias-de-plataforma-pesquisa) — `PLAT`
- [5. NOVA ÁREA — Central de Entregas (Tech Lead)](#5-nova-área--central-de-entregas-tech-lead) — `DEL`
- [6. NOVA ÁREA — Architecture Hub (Arquiteto de Soluções)](#6-nova-área--architecture-hub-arquiteto-de-soluções) — `ARC`
- [7. Pendências externas / homologação](#7-pendências-externas--homologação) — 🚫
- [8. Governança deste documento](#8-governança-deste-documento)

**Parte II:**
- [A. Correção de premissa (a pesquisa não viu o `develop`)](#a-correção-de-premissa-a-pesquisa-não-viu-o-develop)
- [B. Veredito GAP a GAP](#b-veredito-gap-a-gap)
- [C. Decisões de arquitetura/tecnologia](#c-decisões-de-arquiteturatecnologia)
- [D. Roadmap de estudo](#d-roadmap-de-estudo)
- [E. O que implementar, e em que ordem](#e-o-que-implementar-e-em-que-ordem)

---

# PARTE I — Ledger de Funcionalidades

## 0. Plataforma & núcleo (fundação)

*Base já construída e provada (Fases F0–F11). Agrupada em alto nível — a evidência fatia-a-fatia vive em
`docs/backend/execution/PROGRESS.md` e `evidence/**`.*

| ID | Funcionalidade | Status | Origem | Evidência / módulo |
|---|---|---|---|---|
| CORE-01 | Motor de execução durável (leases, fencing, checkpoints, timers, retry, dead-letter, replay) dual-provider | ✅ | M13 | `IDurableExecutionEngine`, GNG-2; `evidence/F1-*` |
| CORE-02 | Persistência dual SQLite↔PostgreSQL (paridade completa, 34+ migrations, modo servidor) | ✅ | M13 | F10-1..5; `evidence/F10-*` |
| CORE-03 | Inbox/Outbox/ledger transacional + auditoria SHA-256 encadeada | ✅ | M13 | F1-TRANSACTIONAL; `Harness.Modules.Governance` |
| CORE-04 | Cadeia de trabalho Solicitação→Demanda→Tarefa→Instrução→Tentativa→Revisão (append-only, correção imutável, actor–critic) | ✅ | M13 | EP-09; `Harness.Modules.Coordination` |
| CORE-05 | Workflows: definições versionadas, fases, gates não-contornáveis, objetivos, progresso ponderado, draft/publish/lifecycle | ✅ | M13 | EP-10, F2-WF, V3-8.11; `Harness.Modules.Workflows` |
| CORE-06 | Documentos versionados imutáveis + aprovações + classificação + edição manual | ✅ | M13 | F1-DOC, V3-8.6; `Harness.Modules.Documents` |
| CORE-07 | Realtime SignalR sequenciado + snapshot/delta + replay pós-restart | ✅ | M13 | F1-REALTIME; ADR-013 |
| CORE-08 | Execução isolada real: worktree Git + sandbox Docker + executor Codex/CLI + cleanup fail-closed | ✅ | M13 | F2-DOGFOOD-1d/1e; ADR-011; `Harness.Modules.Execution` |
| CORE-09 | Orquestração: catálogo de 6 personas, Chief atômico por projeto, organograma, handoff com fencing | ✅ | M13 | F2-ORCH, V3-8.10; `Harness.Modules.Agents` |
| CORE-10 | Contas de agente por alias, executores reais multi-provedor, cota/cooldown/scheduler explicável | ✅ | M13 | ADR-021, N4; `AgentAccountScheduler`, `AccountAvailabilityLedger` |
| CORE-11 | Loop autônomo do Chief (backlog `Ready`→delega→verifica cota→segue) — **nasce DESLIGADO** | ✅ | M13 | `ChiefBacklogLoopService`; commit `ddaf0ee` |
| CORE-12 | Providers/contas/modelos/roteamento/budgets + effort tipado + seleção por agente | ✅ | M13 | F2-PROV, V3-8.13; `Harness.Modules.Providers` |
| CORE-13 | Ferramentas/skills/plugins/MCP (catálogos, policy/allowlist/sandbox, checksum/risk) | ✅ | M13 | F2-TOOL; ADR-009; `Harness.Modules.Tools` |
| CORE-14 | Segurança/hardening: security headers/CORS, scan de segredos, SAST, SBOM, guarda de ações invioláveis | ✅ | M13 | F11-1..7, F3-2; `AutonomousActionGuard` |
| CORE-15 | Desktop: Launcher self-contained, publish macOS, ciclo install/update/uninstall com backup/rollback | ✅ | M13 | F7; `DESKTOP.md` |
| CORE-16 | Licenciamento Ed25519 assinado, ativação/validação offline, revogação | ✅ | M13 | F8; `Harness.Modules.Licensing` |
| CORE-17 | Canais externos: gateway + adaptador Telegram (**operacional, provado**) + adaptador Teams | ✅ | M13 | F9; `@SystemPoseidon_bot` provado 2026-07-22 |
| CORE-18 | Multiusuário: modo servidor, RBAC/ABAC (admin/member), rate limit, carga 30 usuários, maquinaria OIDC (IdP fake) | ✅ | M13 | F10-6..8 |
| CORE-19 | Prototipação + referências visuais (galeria, lifecycle, waiver) | ✅ | M13 | F2-PROT, F5; `Harness.Modules.Prototyping` |
| CORE-20 | Rodar projeto (.NET/Node/Python/Java/Docker/Compose) com health probe e fallback por agente | ✅ | M13 | F6; run targets |

---

## 1. Golden Path — primeiro uso até execução real do Chief

*A "última grande rodada funcional" antes de validar o comportamento real do Chief. Achados da homologação
(P1/P2). Backend já entregou as fatias A/B/C1; C2/C3 e secundárias pendentes.*

**Fluxo-alvo:** Instalar → Perfil → Organização → Projeto → Provider → Modelo → Workflow → Conversa → **Chief responder de verdade** → Demanda → Delegar → Executar → Critic → Gate.

| ID | Funcionalidade | Status | Prio | Origem | Evidência / Ref |
|---|---|---|---|---|---|
| GP-01 | Prontidão canônica (`ConfigurationState`/`ReadinessStep`, blockers/nextAction tipados, `GET /projects/{id}/readiness`) | ✅ | P1 | GP | Fatia A, `06ac3f0`, ADR-017 |
| GP-02 | Fim do catálogo simulado apresentado como real (instalação vazia honesta, fail-closed) | ✅ | P1 | GP | Fatia B, `807edb9`, ADR-018, migration 0047 |
| GP-03 | Separar confirmação de transporte da resposta real do Chief (executor simulado fora do pacote normal) | ✅ | P1 | GP | Fatia C1, `3de12f0`, ADR-019 |
| GP-04 | Turno bloqueado **tipado** (202 `state=blocked` com `blockers[]`/`nextActions[]`), não 400 | 🟠 | P1 | GP | Fatia C2 — **pendente** (`chief_turn_blocks`, migration 0048) |
| GP-05 | Emissão real dos 7 eventos do ciclo do turno (`message.received`→`model.responded`, `chief.turnStateChanged`) | 🟠 | P1 | GP | Fatia C3 — **pendente** |
| GP-06 | Execução real do Chief provada end-to-end (resposta do modelo → demanda → tarefas → evidência → critic → gate) | 🟠 | P1 | GP | Depende de C2/C3 + credencial real (C5 condicional) |
| GP-07 | Gate E2E do golden path **sem demo** + package gate (nenhum estado "Simulated" como real) | ⚪ | P1 | GP | S15; `GOLDEN_PATH_HANDOFF.md §6` |
| GP-08 | Projeto exige organização (não abrir formulário com select vazio; etapa "crie sua primeira organização") | ⚪ | P1 | GP | Frontend + pré-condição backend |
| GP-09 | Workflow recomendado versionado ("Software Delivery Standard") pré-selecionado na criação do projeto | ⚪ | P1 | GP/M13 | backlog card (high) |
| GP-10 | Orquestrador com estados reais (`Não configurado`/`Aguardando provider`/`Aguardando workflow`/`Pronto`/`Em execução`/`Degradado`); "simulado" rotulado como simulado | 🟡 | P1 | GP | Parte via ADR-017/019; UI pendente |
| GP-11 | Readiness do Chief na UI ("Chief ainda não está pronto — falta ✓Org ✓Projeto ✕Provider…", cada item com CTA) | ⚪ | P1 | GP | Frontend (consome GP-01) |
| GP-12 | Campo "criatividade" só permanece se tiver efeito real; senão remover (renomear p/ "Nível de autonomia/exploração" com explicação) | ⚪ | P2 | GP | ADR-020 (hoje: não existe no backend; não introduzir só-visual) |

---

## 2. Catálogos, agentes e execução real

*Seções P1 secundárias do golden path — pré-requisitos da Central de Entregas.*

| ID | Funcionalidade | Status | Prio | Origem | Ref |
|---|---|---|---|---|---|
| CAT-01 | Binding real de effort (valor traduzido enviado ao provider; "não suportado" explícito por modelo; nunca ignorado em silêncio) | 🟡 | P1 | GP/M13 | V3-8.13 (catálogo ✅); prova end-to-end pendente |
| CAT-02 | Definições built-in completas (personas com ~14 campos hoje vazios, coluna `owner`, defaults por papel) | ⚪ | P1 | GP/M13 | backlog card (medium) |
| CAT-03 | Auto-slug de `agent_key` (identificador técnico estável, gerado do nome, campo avançado, bloqueado após 1º uso) | ✅ | P2 | GP | `AgentKeyGenerator` (entregue) |
| CAT-04 | Catálogos reais de team/specialty (não listas hardcoded); CTA "criar" quando faltar; retorno ao draft do agente | ⚪ | P1 | GP/M13 | backlog card (medium) |
| CAT-05 | Modelos/tools/skills vindos de catálogos reais; "gerenciar/criar" quando não encontrar | 🟡 | P2 | GP | Providers/Tools ✅; wiring de "criar-e-voltar" pendente |
| CAT-06 | Import/export de definições de agente (JSON + YAML, round-trip idempotente, validação, colisão por auto-slug) | ⚪ | P2 | GP/M13 | backlog card (medium) |
| CAT-07 | Metadata de atividade recente (read-model paginado por projeto) | ⚪ | P3 | GP/M13 | backlog card (low) |
| CAT-08 | Wizard de definição de agente (Identidade→Papel/Especialidade→Comportamento→Capacidades→Execução→Revisão) | ⚪ | P2 | GP | Frontend |
| CAT-09 | Papéis do runtime = Chief + Especialista; novos "papéis de negócio" viram especialidade/template/persona (não enum arbitrário) | 🟡 | P2 | GP | Confirmar suporte do runtime; ADR-022 |
| CAT-10 | Templates & assistência na definição (usar template, baixar JSON/YAML, importar, validar/pré-visualizar, pedir ajuda ao Chief → **rascunho**, nunca publicação automática) | ⚪ | P3 | GP | Frontend + backend |
| CAT-11 | Quadro: operações em lote + export CSV Excel-compatible **server-side** (hoje client-side) | ⚪ | P3 | M13 | backlog card (low) |

---

## 3. UX / navegação guiada

*Achados P2 da homologação. Frente frontend (`frontend/**`) — fora do escopo de escrita do backend; listado
aqui só como requisito de produto.*

| ID | Funcionalidade | Status | Prio | Origem |
|---|---|---|---|---|
| UX-01 | CTA único por ação (eliminar botões duplicados) | ⚪ | P2 | GP |
| UX-02 | Botão Voltar + breadcrumbs | ⚪ | P2 | GP |
| UX-03 | Slug renomeado para "Identificador da URL", gerado automaticamente, explicado | ⚪ | P2 | GP |
| UX-04 | Branding compreensível: logo por **upload** (não só URL), cores por seletor+HEX (primária/secundária), extração da logo com confirmação, tipografia por presets explicados | ⚪ | P2 | GP |
| UX-05 | Plano (Free/Pro/Enterprise) **read-only**, derivado da licença/capabilities — usuário comum não escolhe | ⚪ | P2 | GP |
| UX-06 | Atividades humanizadas (não expor eventos internos crus ao usuário) | ⚪ | P2 | GP |
| UX-07 | Chat inicial com orientação (não "bloqueado sem explicação") | ⚪ | P2 | GP |
| UX-08 | Cards vazios com ações (nunca card morto) | ⚪ | P3 | GP |

---

## 4. Melhorias de plataforma (pesquisa)

*Derivadas da **Parte II** deste documento. Melhoram o **comportamento do Chief** sem trocar o núcleo.
Sequência: entram em paralelo seguro ao golden path e logo após.*

| ID | Funcionalidade | Status | Prio | Origem | Nota |
|---|---|---|---|---|---|
| PLAT-01 | Artefato de **feature-list/plano durável por projeto** + agente inicializador que decompõe a demanda (Chief trabalha 1 feature/vez) | 🟡 | P2 | PZ (GAP 1) | Board relacional já existe; falta o artefato de plano + planner. Este documento é o 1º passo no nível de produto. |
| PLAT-02 | **`IContextStrategy`** do Chief: compaction por threshold, tool-result clearing, note-taking externalizado no estado | ⚪ | P2 | PZ (GAP 2) | **Maior retorno comportamental.** Memória crítica no store, não na compaction. |
| PLAT-03 | Memória de longo prazo como **índice derivado** (pgvector no modo servidor): decisões/convenções/retrospectivas recuperáveis — nunca fonte de verdade | 🔵 | P3 | PZ (GAP 6) | Sem banco vetorial dedicado; pgvector basta. |
| PLAT-04 | **Evals + métricas por feature + stuck detection** (LLM-as-judge, sucesso/custo/latência por feature, detecção de loop semântico) | 🟡 | P2 | PZ (GAP 7) | Guardrails já fortes; falta a camada de medição. Pré-requisito de qualidade da Central de Entregas. |
| PLAT-05 | **Spike:** avaliar Microsoft Agent Framework/Semantic Kernel como camada de agente/LLM .NET | 🔵 | P4 | PZ | **Ressalva:** modelo atual é CLI-subprocesso (ADR-021), não LLM-direto → valor menor. Só avaliar, não adotar. |
| PLAT-06 | `docs/agents/` — definições declarativas de subagentes (system prompt+tools+modelo+permissões) espelhando `agent_definitions` | 🟡 | P3 | PZ | Personas no DB ✅; falta o espelho documental versionável. |

> **Nota de maturidade:** os GAPs 3 (contexto isolado/handoff), 4 (maker-checker/actor–critic) e 5
> (verificação como gate) da pesquisa **já estão implementados** no Poseidon — ver Parte II §B.

---

## 5. NOVA ÁREA — Central de Entregas (Tech Lead)

> **`NV` — expansão de escopo além da missão v1.3.** Apoia o papel de **Tech Lead / dono do resultado**:
> receber um desenvolvimento da coordenação, acompanhar prazo/meta/objetivo, e entregar como produto
> completo (plano, previsões honestas, documentação, métricas, valor) para uma coordenação que **não
> acessa o Poseidon**. **Não** substitui o PO nem vira outro Kanban. Reusa: Workflows, Documents,
> Coordination, Notifications, Governance, Agents.

**Prioridade macro:** entra **depois** do golden path (o próprio proponente recomenda). MVP = DEL-01/02/03/04/05.

| ID | Funcionalidade | Status | Prio | Reuso |
|---|---|---|---|---|
| DEL-01 | **Portfólio de Entregas** — lista de entregas por saúde/previsibilidade (não cards de dev); visão "Precisa da minha atenção" (acesso a banco pendente, ambiente, decisão arquitetural, doc ausente, data comprometida, escopo, dependência, sem dono, sem atualização) | ⚪ | P2 | Coordination/Projects |
| DEL-02 | **Projeto 360** — abas: Resumo executivo, Plano&marcos (com **histórico de previsão**, nunca sobrescrita silenciosa), Saúde técnica (10 indicadores), Riscos&dependências, Decisões, Documentação (checklist), Valor&métricas | ⚪ | P2 | Documents/Workflows |
| DEL-03 | **Daily Copilot** — briefing pré-daily (mudanças, atenção, perguntas recomendadas), captura durante (marcações: acesso/dependência/decisão/prazo/escopo/doc/risco), resumo pós-daily; **não** cria/atualiza cards do PO por padrão | ⚪ | P2 | Agents/Conversations |
| DEL-04 | **Central de Relatórios** — status executivo semanal, relatório de marco, prontidão homologação, prontidão produção, dossiê de encerramento; formatos **PDF/PPTX/Word/Teams/e-mail/CSV/ZIP**; controle de envio (audiência/classificação/versão/aprovado por/snapshot) com **aprovação humana antes do envio** | ⚪ | P2 | Documents/Notifications |
| DEL-05 | **Encerramento & Valor** — dossiê (solicitado × entregue, arquitetura final, tempo, decisões, métricas, defeitos, valor, riscos residuais, lições, responsáveis de suporte) | ⚪ | P3 | Documents |
| DEL-06 | **Métricas** — DORA (change lead time, deployment frequency, failed-deploy recovery, change fail rate, deploy rework) por aplicação + próprias (precisão de previsão, % doc, defeitos homolog, mudanças de escopo, dependências, tempo aguardando acesso, valor planejado×realizado) | ⚪ | P3 | Governance |
| DEL-07 | **Workflow de entrega técnica** (11 fases: Recebimento→Baseline→Planejamento→Execução acompanhada→Prontidão homolog→Homolog→Prontidão prod→Produção→Estabilização→Encerramento→Revisão de benefícios), com gates/documentos obrigatórios | ⚪ | P3 | Workflows (template novo) |
| DEL-08 | **Agentes Delivery** (sob demanda): Tech Lead Copilot, Daily Intelligence, Risk & Dependency Analyst, Delivery Forecast, Quality & Release Auditor, Documentation Steward, Executive Reporting, Benefits Analyst | ⚪ | P3 | Agents |
| DEL-09 | **Previsão honesta** — o Delivery Forecast **nunca inventa data**: expõe data + confiança + base (% marcos, dependências abertas, variação média, validações pendentes) | ⚪ | P2 | Agents/Evals (PLAT-04) |
| DEL-10 | **Reporte à coordenação sem acesso à plataforma** — entrega externa por canal (Teams/e-mail) a partir da Central de Relatórios | ⚪ | P2 | Notifications/CORE-17 |

---

## 6. NOVA ÁREA — Architecture Hub (Arquiteto de Soluções)

> **`NV` — expansão de escopo além da missão v1.3.** Apoia o papel de **Arquiteto**: analisar/editar a
> arquitetura proposta pelos agentes, desenhar do zero com templates, e **mapear os ~200 sistemas
> existentes** (funcionalidades, conexões, duplicidade, candidatos a unificar/abandonar). **Princípio:**
> manter um **modelo arquitetural estruturado** (elementos+relações), não imagens; diagramas são *views*
> desse modelo (C4/ArchiMate). Reusa: Prototyping/visual_references, Documents/ADR, Agents, Projects.

**Prioridade macro:** entra **após** a Central de Entregas. MVP = ARC-02/03 (catálogo + Sistema 360) e ARC-04/05 (Studio + edição humana).

| ID | Funcionalidade | Status | Prio | Reuso |
|---|---|---|---|---|
| ARC-01 | **Modelo arquitetural estruturado** (elementos + relacionamentos versionados; diagramas = views; consulta de dependências; reuso de elementos) | ⚪ | P3 | novo (base p/ todo o Hub) |
| ARC-02 | **Mapa Corporativo de Sistemas** (~200): catálogo, mapa por domínio, mapa de capacidades, grafo de integrações, heatmaps (tech obsoleta, sem dono, sem doc, crítico, custo, incidentes, duplicidade, risco, bus-factor) — visão System Landscape | ⚪ | P3 | Projects/novo |
| ARC-03 | **Sistema 360** — por sistema: Negócio, Tecnologia, Integrações, Operação (SLA/incidentes/backup/DR), Dados (PII/sensível/retenção), Governança (doc/ADR/riscos/última revisão/confiança) | ⚪ | P3 | Documents |
| ARC-04 | **Architecture Studio** — canvas com elementos/propriedades; views **C4** (Contexto/Containers/Componentes/Deployment/Dynamic) e **ArchiMate** (negócio/aplicação/dados/tecnologia/motivação); vistas extras (dados, sequência, segurança/trust boundaries, infra, rede, custos, observabilidade) | ⚪ | P3 | Prototyping/visual_references |
| ARC-05 | **Edição humana da arquitetura proposta pelo agente** — diff, **lock de elemento** (agente não sobrescreve bloqueado), documentos afetados viram **drafts**, justificativa em mudança crítica, histórico/rollback, **proposta × implementada separadas**, impacto apresentado **antes** de atualizar | ⚪ | P2 | Documents/Governance |
| ARC-06 | **Descoberta de sistemas existentes** — fontes (repo/docs/OpenAPI/schema/Dockerfile/pipelines/IaC/logs/inventário/entrevistas/planilhas/diagramas); saída com **confiança + evidência + perguntas pendentes** por informação | ⚪ | P3 | Agents/Execution |
| ARC-07 | **Insights & Racionalização** — sobreposição funcional, unificação, desativação, tech fora de suporte, integrações ponto-a-ponto, acesso direto a banco alheio, SPOF, custo×uso; classificação **Manter/Modernizar/Consolidar/Substituir/Desativar/Investigar**; agente **sugere com impacto, nunca decide sozinho** | ⚪ | P3 | Agents |
| ARC-08 | **Padrões & Decisões** — ADRs corporativos e padrões reutilizáveis (o mesmo problema resolvido de forma consistente) | ⚪ | P4 | Documents/ADR |
| ARC-09 | **Agentes Architecture** (sob demanda): Architecture Chief, Discovery, Solution Architect, Enterprise Architecture, Integration Architect, Data Architect, Security Architect, Infrastructure Architect, Rationalization Analyst, Architecture Critic, ADR Writer | ⚪ | P3 | Agents |
| ARC-10 | **Integração Delivery↔Architecture** — nova entrega consulta o portfólio dos 200 sistemas (reuso/duplicidade), arquitetura aprovada vira **baseline da entrega**, produção atualiza o **AS-IS**, encerramento compara **proposta × implementação** | ⚪ | P3 | Coordination/Workflows |

> **Nota:** o Poseidon **já tem uma fase de arquitetura** (o agente cria arquitetura a partir dos requisitos)
> + prototipação + referências visuais + documentos/ADR. O Architecture Hub **estende** isso com o modelo
> estruturado e as visões macro/micro — não é greenfield.

---

## 7. Pendências externas / homologação

*Não são "código faltando" — dependem de aceite humano ou credenciais de terceiros.*

| ID | Item | Status | Origem | Bloqueio |
|---|---|---|---|---|
| EXT-01 | Homologação visual **GNG-3** (aceite humano no navegador) — destrava os 20% de "Homologado" | 🚫 | M13 | Você validar o fluxo real e registrar aceite |
| EXT-02 | **GNG-4**: macOS limpo + Developer ID/notarização Apple | 🚫 | M13 | Identidade/certificado Apple |
| EXT-03 | **GNG-6**: aceite global / DoD | 🚫 | M13 | Aceite humano |
| EXT-04 | Smoke OIDC com **Entra ID real** | 🚫 | M13 | Tenant/Client ID |
| EXT-05 | Smoke **Teams** com Azure Bot/M365 real | 🚫 | M13 | Credenciais Microsoft |
| EXT-06 | Smoke fallback F6 / effort com executor/modelo real que consuma cota | 🚫 | M13 | Credencial de provider |
| EXT-07 | **Ponte de notificação por e-mail** (SMTP `mdomingos@prumma.com.br` → `mlima.terceiro@trensrio.com.br`) | 🚫 | OPS | Relay SMTP autenticado do domínio prumma (segredo só no Keychain) |

---

## 8. Governança deste documento

- **Papel:** este é a **fonte da verdade canônica de FUNCIONALIDADES**. Execução/evidência continua em
  `docs/backend/execution/PROGRESS.md` (por fatia) e `ROADMAP_PROGRESS.md` (%). Requisitos originais na
  **missão v1.3**.
- **Quem edita:** você (humano). Priorize, marque status, adicione/remova linhas. Mantenha os **IDs estáveis**
  (não renumere; para aposentar, marque `🗑️ deprecado` em vez de apagar).
- **Coerência com o plano original:** as áreas 5 e 6 (Delivery/Architecture) e a área 4 (plataforma) são
  **expansões novas** (`NV`/`PZ`) — **não** estavam na missão v1.3. Estão marcadas para você decidir se
  entram no escopo canônico.
- **Rastreabilidade → board:** cada `ID` pode virar card no board (`POST /api/v1/tasks`), mantendo o ID no título.
- **Garantia de visibilidade no boot (feito):** a **memória durável do agente** (carregada no início de
  toda sessão) tem um ponteiro dedicado para este arquivo — `[[source-of-truth]]`. Isso garante que o Chefe
  o **enxergue** e o consulte quando o usuário perguntar sobre funcionalidades/escopo. O agente **não** age
  automaticamente por causa dele. Observação técnica: o `CLAUDE.md` é um **arquivo gerado** (de
  `governance/core.md` + `rules/` + `manifest.yaml`) e **não** deve ser editado à mão — por isso a garantia
  vive na memória, não numa edição do `CLAUDE.md`.
- **Follow-up de registro formal (requer aprovação + roda gates — NÃO feito):** para tornar este arquivo
  *canônico no mecanismo de governança* (não só na memória do agente), adicionar um ponteiro em
  `governance/core.md`, registrar a entrada no `governance/manifest.yaml` como `authority: canonical`/
  `status: active`, e rodar `sync → generate → sync` (regenera `CLAUDE.md`/`docs/INDEX.md`). Isso aposenta a
  dependência da missão v1.3 marcada *"histórica"*. É mudança de governança que **roda gates** —
  recomendo fazer juntos, com você presente, após sua revisão do conteúdo.

---

# PARTE II — Alinhamento Pesquisa × Arquitetura

> **O que é.** A análise cruzada do *Relatório Consolidado de Pesquisa Profunda* (roadmap de estudo,
> benchmark de sistemas, gap analysis) contra a arquitetura **real** do Poseidon (`develop`). Fundamenta
> as entradas `PLAT-xx` da Parte I. Objetivo: produto de produção, não POC.

## A. Correção de premissa: a pesquisa não viu o `develop`

A pesquisa declara (caveat) que **não acessou a branch `develop`** — só o `main` (README). Por isso
superestimou o que falta: vários "GAPs" apontados como incertos **já estão implementados e provados**.
Situação auditável: **Implementado 100% · Validado ~99,8% · Integrado ~99% · Homologado 0%** → **~79,6%**.
O que falta não é núcleo de agente; é (a) fechar o golden path, (b) homologação humana, (c) um punhado de
melhorias comportamentais de alto retorno.

## B. Veredito GAP a GAP

| GAP da pesquisa | Situação real no Poseidon | Veredito |
|---|---|---|
| **1. Plano/decomposição persistido (feature list + progress + task board relacional)** | Board relacional já existe (`solicitations→demands→tasks→attempts`), workflows com fases/gates/objetivos, projeções de progresso; o Chief já pega **uma tarefa por vez**. **Falta**: o *artefato feature-list por projeto* + *agente inicializador* de decomposição. | **PARCIAL — adotar o que falta** (PLAT-01). |
| **2. Gestão de contexto do Chief (compaction, clearing, note-taking)** | Estado do turno é durável (`chief_turn_blocks`, mailbox, lease) → memória externalizada existe. **Falta**: estratégia explícita de compaction/threshold/clearing. | **ADOTAR — maior retorno comportamental** (PLAT-02). |
| **3. Contexto isolado por subagente + handoff com contrato** | **Já é força**: worktrees/contas isolados, briefing estruturado (`PersonaCardComposer`, ADR-022), retorno tipado (`CriticReviewContract`), só o Chief fala com o usuário. | **JÁ FEITO** ✅ |
| **4. Loop maker-checker / evaluator-optimizer** | **Já implementado**: actor–critic obrigatório de risco médio pra cima, critic independente, correção por instrução imutável, 2ª tentativa. | **JÁ FEITO** ✅ (falta só explicitar limite de iteração + budget, já parcial via cota/watchdog/DLQ). |
| **5. Testing/verificação end-to-end como gate** | **Já implementado**: gates não-contornáveis (Default-FAIL), sandbox Docker, run targets com health probe. | **JÁ FEITO** ✅ |
| **6. Memória de longo prazo como índice derivado** | **Não existe** índice vetorial/semântico entre projetos. | **ADOTAR** (PLAT-03) — pgvector no servidor, *índice derivado, nunca fonte de verdade*. |
| **7. Evals + guardrails + stuck detection** | Guardrails fortes já existem (`AutonomousActionGuard`, consistência em 3 camadas, SAST, scan de segredos); stuck de **execução** via watchdog/lease. **Falta**: evals LLM-as-judge, métricas **por feature**, stuck **semântico**. | **PARCIAL — adotar evals/métricas/stuck** (PLAT-04). |

**Resumo:** dos 7 GAPs, **3 já feitos** (3, 4, 5), **2 parciais** (1, 7), **2 novos de valor real** (2, 6). Nenhum exige trocar o núcleo.

## C. Decisões de arquitetura/tecnologia

**C.1 NÃO trocar o núcleo durável — confirmado.** Temporal/Orleans/LangGraph resolvem o que o Poseidon já
reimplementou (ADR-005). Servem como **catálogo de padrões**, não dependência. *Gatilho de reavaliação:*
só se a escala horizontal (milhares de execuções/mês, workers distribuídos) virar gargalo medido.

**C.2 Microsoft Agent Framework / Semantic Kernel — avaliar, NÃO adotar às cegas.** A pesquisa assumiu
agentes por **LLM direto**; o Poseidon delega a **CLIs agênticas completas** via subprocesso
(`ProcessExternalAgentExecutor`, `IAgentExecutor`) com isolamento por conta/cota (ADR-021) — a espinha do
diferencial de governança. O valor do SK (function-calling/MCP client) é **menor** aqui. **Decisão:** manter
`IAgentExecutor` como fronteira; **avaliar** SK só se surgir caminho de *agente por API direta* (ex.: executor
leve p/ planejamento/roteamento barato). Registrar como **PLAT-05 (spike)**, não adoção.

**C.3 pgvector no modo servidor — adotar quando PLAT-03 entrar.** Cobre o índice derivado sem novo serviço.
Não adicionar banco vetorial dedicado enquanto pgvector atender.

**C.4 `IContextStrategy` explícita — adotar (PLAT-02).** Compaction/clearing/note-taking atrás de interface
testável e trocável por modelo. Maior impacto no comportamento do Chief em horizonte longo.

## D. Roadmap de estudo

Ordem recomendada, **sem** treinar modelos: **context engineering → orquestração hierárquica/handoffs →
loops de auto-correção → memória → evals/guardrails → MCP a fundo.** Adiar: ML/DL profundo, interno do
Transformer, fine-tuning/LoRA, RAG pesado. Fontes .NET-first: guias de engenharia da Anthropic + doc do
Microsoft Agent Framework.

## E. O que implementar, e em que ordem

> **Regra de sequência:** o **golden path** vem primeiro; áreas novas (Delivery/Architecture) e melhorias de
> plataforma vêm **depois**.

1. **Agora (rodada atual):** fechar o golden path — GP-04/05/06/07 + catálogos/effort/built-ins/import-export + workflow na criação (§1–§2).
2. **Habilitador de alto retorno, em paralelo seguro:** **PLAT-02** (`IContextStrategy`) e **PLAT-01** (feature-list durável + inicializador).
3. **Depois do golden path:** **PLAT-04** (evals + métricas por feature + stuck) — pré-requisito de qualidade da Central de Entregas.
4. **Contínuo:** **PLAT-03** (memória vetorial derivada) e **PLAT-06** (`docs/agents/`).
5. **Spike isolado, sem compromisso:** **PLAT-05** (avaliação SK).

**Conclusões:**
- O Poseidon **não precisa de mais framework de IA**; precisa de **harness/context/loop engineering** sobre o motor durável que já tem.
- Implementar **PLAT-01/02** é o que mais destrava o comportamento do Chief hoje.
- **Não reescrever o núcleo.** Padrões de mercado = referência; SK = avaliação, não adoção imediata.
- **Medir** (PLAT-04) sustenta "o Chief é o único responsável pelo sucesso" — e o material de promoção a Arquiteto Sênior.

---

*Unificado em 2026-07-22 (a partir de `FEATURE_LEDGER.md` + `RESEARCH_ALIGNMENT.md`) como fonte da verdade
canônica para revisão humana. Nenhum comportamento de sistema foi alterado na sua criação.*
