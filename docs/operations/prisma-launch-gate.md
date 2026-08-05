# Prisma Launch Gate — relatório de prontidão (2026-08-05)

**Projeto:** Prisma (`01KZ7ZNTXRRQMMA9G1TQ2H5BFW`, TrensRJ/GRC) · **Prazo:** 2026-08-20 ·
**Estado ao fim do gate:** `paused`, 0 cards, 0 tentativas, 0 despacho externo.

## Resultado por bloco

| Verificação | Resultado | Evidência |
|---|---|---|
| Graph para o Prisma | **ON** | `Harness__Graph__ProjectionEnabled=true` em `~/.harness/poseidon.env` (instalação local; default do produto segue OFF) |
| Graph rebuild | **PASS** | `poseidon graph rebuild` → 33 nós / 52 arestas; **26 Requirements** (`acceptance_criterion`, §16), HumanFact Oracle 19c, artefatos-fonte; ids determinísticos; zero `relates_to` (impossível por CHECK); versão monotônica |
| Deadlock de fase inicial | **NÃO ocorre** | `GraphEarlyPhaseDeadlockTests`: 0/26 sem card ≠ bloqueio; fase inicial despacha; implementação fora de ordem fica presa com o nó exato na razão |
| Rollback da flag | **PASS** | ON→OFF→ON sem destruir a projeção; OFF recusa com código declarado; religar rebuilda idêntico (mesmo teste) |
| Chat com escopo de projeto | **PASS** | Defeito histórico NÃO reproduz no HEAD; prova A/B/A no navegador real (Prisma→Poseidon→Prisma, histórico e anexos nunca atravessam); regressão permanente `chat-project-isolation.spec.ts` (2 viewports) |
| Requirements artifact | **PASS** | `Prisma_Especificacao_Tecnica_MVP.md` role `requirements_source`, aceito, SHA persistido |
| Provided frontend | **PASS** | `bright-vision-interface-main.zip` role `provided_frontend` (conjunto fechado, migração 0127); contrato canônico `docs/product/provided-artifacts.md` (inspecionar/preservar/completar/integrar; substituição proibida); extração protegida (`ZipArtifactExtractor`: zip-slip, symlink, bomba, teto de tamanho/contagem) |
| Rich Intake FLOW 2 | **PASS — no runtime** | `WorkflowPhaseDriver.AssessIntakeAsync` → `RichIntakeAnalyzer` (sinais estruturados, não keyword) → ordem de trabalho VALIDAR→NORMALIZAR→RASTREAR→PREENCHER injetada em cada card de fase; rotas CLOSED/INFER/ASK/DEFER tipadas em `DecisionPolicy` |
| Effective Profile preview | **PASS** | `PrismaEffectiveProfilePreviewTests` resolve da solicitação REAL: Web, frontend obrigatório React, backend .NET, **Oracle como override com autoridade ProjectRequirement**, REST + OpenAPI, proveniência declarada |
| Oracle | **READY** | `prisma-oracle-19c-preflight` up; `select 1 from dual` respondendo como `prisma_app@FREEPDB1`; verificadores sem `Unsupported` (prisma-readiness §2) |
| 26 critérios | **ACCOUNTED** | 26/26 no grafo, 0 cobertos, 0 perdidos, 26 aguardando Planning — correto pré-planejamento |
| QA/NFR/UX readiness | **PASS** | Canon cobre os 16 níveis de teste (`qa-standards.md` §2), refinamento≠feature (`frontend-standards.md`), carga nunca inventada (`qa-standards` §5); lista NFR abaixo |
| Despacho externo | **ZERO** | Nenhuma tentativa criada em toda a missão |

## Dois defeitos REAIS de plataforma que o gate capturou (e corrigiu)

1. **"ios" por substring**: `ProductModalityInference` classificava como **Mobile** qualquer
   texto com "critér**ios**/relatór**ios**/usuár**ios**". O preview do Prisma nasceria Mobile.
   Corrigido (fronteira de palavra) com regressão congelada.
2. **"Oracle (requisito da TrensRJ)" ignorado**: o parser de diretivas não reconhecia
   "requisito de/da" como sinal de decisão — o override de banco do Prisma seria perdido e o
   perfil sairia SQL Server. Corrigido com regressão (o preview inteiro é a prova).
3. *(bônus, da própria plataforma)* `storage_path` relativo quebrava a leitura de anexos no
   navegador de seções da Bruna (Onda 0.7) e no rebuild do grafo — resolvido pela resolução
   canônica com confinamento (`SolicitationAttachmentStorage.Resolve`).

## NFRs do Prisma — classificação (nenhuma pergunta precisa ser enviada agora)

| NFR | Classe | Fonte/premissa |
|---|---|---|
| Autenticação própria sem SSO, provisionamento pela GRC | **ANSWERED** | Spec §14 🔒 |
| Sem integrações externas no MVP | **ANSWERED** | Spec §14 🔒 |
| LGPD: sem dados sensíveis; segurança da informação | **ANSWERED** | Spec §14 🔒 |
| Volumes (~16 riscos, ~120 fatores, ~100 controles, 200+ planos/ciclo) | **ANSWERED** | Spec §3 |
| Usuários simultâneos | **INFERABLE** | Público = perfis internos da GRC/gestores (§7): dezenas, não milhares; registrar premissa no perfil operacional |
| Fuso America/Sao_Paulo em prazos/fechamentos | **ANSWERED** | Spec §14 |
| Retenção da auditoria | **NON_BLOCKING_UNKNOWN** | Default 5 anos ajustável (spec §17.4) |
| Hospedagem (provedor/território) | **NON_BLOCKING_UNKNOWN** | Agnóstico por exigência (§14 🟡); não muda arquitetura |
| Navegadores-alvo | **INFERABLE** | Baseline: evergreen + responsivo; público §12 |
| RTO/RPO | **NON_BLOCKING_UNKNOWN** | Sem exigência na spec; backup/append-only cobertos por persistência §14; formalizar na fase de operação |
| Meta numérica do ITRC | **ANSWERED** | 🔒 sem meta; campo configurável (§17.2) |
| **BLOCKING** | **nenhum** | — |

## Digest de impacto atual (Graph ON, Prisma paused)

- Fase: não iniciada / intake · Requirements: **26** · Cobertura: **0/26** · Stale: **0** ·
  Cadeias de bloqueio: **nenhuma** (não há arestas `depends_on` pré-planejamento) · Lacunas de
  evidência de gate: **não aplicável** (nenhum gate instanciado) · Decisões humanas pendentes:
  **nenhuma classificada ASK**.
- Consultas (`bloqueio`/`impacto`/`gate`/`revalidacao`) respondem vazio-semântico correto —
  nada foi fabricado para demonstrar feature.

## Status do Graph (registro pedido pelo dono)

`IMPLEMENTED · TESTED_BY_REPLAY · NOT_YET_OBSERVED_IN_PRISMA_REAL_RUN` — o replay do run de
empréstimos NÃO é E2E do Prisma; a primeira evidência real virá do próprio run.

## Veredito

**PRISMA READY FOR MATEUS KICKOFF** — falta somente a mensagem do dono na conversa do Prisma
(que despausa e inicia o FLOW 2 real). Nenhum agente foi despachado nesta missão.
