# Poseidon v2 — Perfil de execução Understand → Build → Prove (2026-08-07)

**Status:** norma de implementação da branch `poseidon-v2`. A `develop` está congelada em
`cb0351fe` como produto original; este documento define o que a reconstrução muda e por quê.
**Base de evidência:** `docs/operations/diario-avaliacao-trensrj.md`,
`docs/operations/incidents/INC-EVAL-*.md` e a auditoria independente de 2026-08-07.

## 1. Decisão

A avaliação real (TrensRJ, 05–07/08) provou que o pipeline de micro-cards coloca custo
estrutural no caminho crítico (75% das tentativas mortas por defeito de plataforma; mais
tokens em tentativas reprovadas que aprovadas; 5 aprovações de código em 21h; produto final:
74 linhas de C# no Prisma e um backend Node/memória no Indicadores que violava o
EffectiveProfile e passou 4× em review). A causa mais profunda não foi falta de revisor
independente — foi **verificação apontada para o diff, e não para o produto contra os
requisitos**.

A v2 reduz o núcleo operacional a três responsabilidades:

1. **Understand** — intake mantido quase integral (subsistema mais provado da plataforma):
   artefatos livres → requisitos normalizados, EffectiveProfile com proveniência, acceptance
   criteria, ASK só quando genuíno.
2. **Build** — **um executor persistente forte por projeto**, com contexto completo (spec,
   profile, critérios, frontend fornecido, repo), trabalhando horas na mesma sessão.
   Bruna supervisiona por fora: progresso, cota, bloqueio, crash, retomada.
3. **Prove** — qualidade concentrada em dois mecanismos: gate determinístico de conformidade
   de stack a cada colheita (custo ~zero) e **Product Validator** independente por objetivo
   (recebe requirements originais + acceptance criteria + EffectiveProfile + aplicação
   rodando; valida produto, não diff).

## 2. Princípios de implementação

- **Não construir segunda engine.** O loop novo é um *perfil de política* sobre a engine de
  cards existente (tentativa, branch `task/agent-run-*`, colheita, merge, ledger, evidence,
  recovery de órfãos já existem e estão PROVEN). Um objetivo = um card-objetivo.
- **FREEZE, não DELETE.** Componentes fora do caminho crítico ficam desligados por flag e o
  código permanece: Playbook 9 fases como máquina de estados de runtime, scheduler de
  micro-cards, Council como operação normal, PlanGraph como condição de escrita, review por
  card, roteamento por risk-tier.
- **Evidência contínua.** O ledger e o recorte `EvaluationWindow` continuam medindo tudo; a
  baseline do Dia 1 é o comparativo obrigatório da v2.

## 3. Componentes

### 3.1 Card-objetivo (perfil `objective`)

Card único e grande por objetivo funcional (3–8 por projeto), com:
sem claims de PlanGraph; sem Council; sem review por card; teto de rodadas alto; contexto
montado com o pacote completo do projeto. O primeiro objetivo de todo projeto com
`provided_frontend` é **fatia vertical navegável** (frontend fornecido integrado + login +
1 tela núcleo → API real → banco real) — nunca entrega por camadas.

### 3.2 Gate determinístico de conformidade de stack

Roda a cada colheita/merge do repo do produto, derivado mecanicamente do EffectiveProfile:

| Diretiva do profile | Check |
|---|---|
| Backend .NET | existe `*.csproj` compilável no caminho de produção |
| Oracle Required | driver Oracle referenciado; ausência de persistência "em memória" no caminho de produção |
| Frontend React | `package.json` com `react` + componentes presentes |
| REST + OpenAPI | endpoint `/swagger` ou spec gerada |

FAIL bloqueia o merge com finding explícito. Este gate teria matado o desvio do Indicadores
no primeiro commit, e não no dia 3.

### 3.3 Product Validator

Persona `critic` com a aplicação **rodando** (API + frontend) e browser (Playwright).
Insumos: requirements originais, acceptance criteria, EffectiveProfile, frontend de
referência. Verifica: cobertura de requisitos, navegação real, formulários, estados
vazios/erro, console/network, frontend → API → banco, segurança essencial (auth,
autorização, acesso horizontal), aderência visual. Saída: `PASS` ou findings estruturados.

**Findings voltam ao MESMO executor** (continuidade via `CorrectionBaseBranch`, corrigida em
2026-08-07). **Teto de 3 ciclos** validador↔executor por objetivo; no terceiro FAIL,
escalação por Human Attention — um executor que racionalizou uma interpretação errada não
ganha cota infinita para defendê-la.

### 3.4 Bruna — 4 responsabilidades

Intake · Planning (objetivos + acceptance criteria; responde READY/NEEDS_INPUT) ·
Supervision (vivo? progresso? cota? bloqueado?) · Delivery (aciona validação, consolida
evidência, Ready for Human Acceptance). Sai do papel dela: scheduler, replanejamento
automático fino, council.

### 3.5 Probe de disponibilidade da fleet

Hoje a disponibilidade (`~/.harness/account-availability.json` + DB) é **reativa**: só
aprende quando um run falha. A v2 adiciona probe proativa (`poseidon fleet probe`): invoca
cada conta com prompt trivial, parseia mensagens de cota da CLI (incl. horário de reset,
ex.: "resets 12:40pm"), grava `State`/`CooldownUntil`/`ReasonCode` verdadeiros. Correções
associadas (INC-EVAL-006): resolução de modelo **por provider** — jamais enviar `model_id`
de um provider a outro — e classificação correta (`account_model_unsupported` ≠
`AuthenticationRequired`, estado observado hoje no `worker-codex-frontend`).
**Effective Execution Slots** = contas write-capable ∧ com papel elegível ∧ sem cooldown —
número calculado, exibido e usado pelo dispatch; nunca declarado à mão.

### 3.6 Papéis da fleet na v2

| Conta | Papel v2 |
|---|---|
| `chief-claude-primary` | Bruna/supervisão (chief-orchestrator) |
| `worker-claude-secondary` | executor persistente |
| `worker-codex-frontend` / `worker-codex-critic` | executor (pós INC-EVAL-006) / validador |
| `worker-antigravity-review` | Product Validator |
| `worker-kimi-ui` | executor frontend (quando voltar) |
| `worker-glm-general` | reserva (se probe confirmar cota) |

`allowedRoles` deixa de ser o gargalo silencioso: a v1 operou com **1 conta elegível** para
execução e isso só foi visível post-mortem.

## 4. Critérios de aceite da plataforma v2

1. Projeto criado dos insumos originais chega a READY de intake sem regressão (suite atual).
2. Kickoff → primeiro card-objetivo despachado sem nenhuma tentativa morta por
   `scopeconflict`/persona/model-effort (as 3 causas de 257 mortes do Dia 1).
3. Gate de conformidade reprova mecanicamente um repo com stack divergente do profile
   (teste com o caso real do Indicadores como fixture).
4. Validator reprova objetivo com requisito faltante e o retry chega ao mesmo executor com
   os findings (verificado por teste de integração).
5. 3º FAIL de validação abre Human Attention automaticamente.
6. `poseidon fleet probe` popula disponibilidade correta das 7 contas sem nenhum run real.
7. `tools/backend/verify.sh` verde na branch.
8. Métrica final (EvaluationWindow): fatia vertical validada nos dois produtos com tokens
   por requisito e intervenções humanas **menores que a baseline do Dia 1**.

## 5. Migração

1. Flags de freeze + perfil `objective` na engine (sem migração destrutiva de schema).
2. INC-EVAL-006 + probe da fleet + papéis v2.
3. Gate de conformidade de stack.
4. Product Validator.
5. Arquivar projetos da avaliação (snapshot forense já em
   `Arquivos-Historicos-Projetos/avaliacao-trensrj-descartada-20260807/`), recriar pristine
   dos insumos, kickoff no perfil novo.
6. Ao final: `Poseidon-Apresentacao-V2` reescrita com o estado real da branch.

## 6. Mapa de integração (levantado do código em 2026-08-07)

Fatos do engine que sustentam o desenho "perfil, não segunda engine":

- **O dispatch NÃO filtra por fase**: `ChiefBacklogLoopService.RunCycleAsync` consome
  qualquer card `ready` (`board.PageTasksAsync(..., "ready", ...)`, sem `phaseName`). A
  máquina de 9 fases (`WorkflowPhaseDriver.DriveAsync`) governa a EXISTÊNCIA de cards e o
  fechamento de gates da fase ativa — sem binding/run de workflow, ela retorna cedo e nada
  bloqueia. Cards sem fase são aceitos por contrato (`ActivePhaseResolver`). Logo: cards-
  objetivo criados fora do Playbook executam na esteira normal, e congelar as 9 fases =
  não criar binding, sem tocar em código do driver.
- **Ponto único de resolução modelo/effort**: `ChiefBacklogLoopService` (~linha 1047) +
  `ModelRouter.Route` — corrigido nesta branch (INC-EVAL-006, commit 191cb916).
- **Cadeia de review determinística** (`ReviewAwaitingAttemptsAsync`): gate documental →
  **gate de conformidade de stack (novo, commit d8294618)** → diagnóstico de código →
  varredura de segredo → placeholders → review LLM (`orchestrator.ReviewAsync`, timeout
  10min). Reprovação determinística usa `ApplyReviewVerdictAsync` com
  `LayerResult(Deterministic, Fail, …)` e ReasonCode em `AppliableReviewReasons`.
- **Continuidade de correção**: `CorrectionBaseBranch` (público, estático) reaproveita a
  branch da última tentativa `failed` — o mecanismo "findings voltam ao mesmo executor" já
  existe e está corrigido.
- **Perfil efetivo**: `IProjectEffectiveProfileStore.GetCurrentAsync` +
  `ProjectEffectiveProfile.FromJson` (null = fail-closed); gravado por
  `ProductDeliveryEvaluator.MaterializeAsync` (fase ≥3) com `ScopeIntegrityGuard`.
- **Evidência de produto**: `RepositoryEvidenceCollector` (existência/forma) +
  `ProductDeliveryGate` (proveniência/trust, Default-FAIL) + `PhaseGatePolicy`.
- **Cota/disponibilidade**: adaptadores de CLI traduzem stderr em `ExternalFailureKind`;
  `AgentRunOutcomeClassifier.Classify` → `AccountAvailabilityLedger`
  (`~/.harness/account-availability.json`) + `CapacityManager` (memória) +
  `IModelInvocationStore` (banco). A probe proativa da v2 reusa essa tríade.
- **`EvaluationWindow` (Modules.Workflows.Product)**: recorte puro do ledger, pronto e SEM
  consumidor em `src/` — adotar como recorte oficial das métricas v2.
- **Parâmetros hoje hardcoded, candidatos ao perfil `objective`**: backoffs (2min/5min/32min),
  `MaximumOperationalReplanRounds = 4`, timeout de review 10min, truncamento de diff 160kB;
  `AgentRunSettings` já expõe `RunTimeout` (30min), `RunNoProgressTimeout` (15min),
  `ContextTokenBudget` (16k) — todos pequenos demais para executor persistente; o perfil
  `objective` os eleva por card-type, não globalmente.

## 7. Estado da implementação nesta branch

| Componente | Estado |
|---|---|
| INC-EVAL-006 (resolução cross-provider) | **FEITO** — 191cb916 |
| Gate de conformidade de stack | **FEITO** — d8294618 (fixture = entrega real do Indicadores) |
| Perfil `objective` (card-objetivo, timeouts, rounds, contexto) | em implementação |
| Product Validator (app rodando, teto 3 ciclos) | pendente |
| Probe de disponibilidade da fleet | pendente |
