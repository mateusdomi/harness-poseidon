# N4 — scheduler explicável, quotas honestas e fallback

Data: 2026-07-22. Branch: `develop`. Base: `bda6aac` (N3). Continuação de ADR-021 (CA-6).

## Objetivo

Fechar a SELEÇÃO de contas por código explicável e fail-closed, com estados fechados,
modelo de cota honesto (Unknown, nunca percentual inventado) e fallback tipado. Prepara o
Piloto 2 concorrente.

## Modelo de cota (5.1) — `AccountQuotaSnapshot`

Snapshot com proveniência: `Source`, `ObservedAt`, `Status` (`QuotaStatus`:
Unknown/Available/NearLimit/Exhausted), `Confidence` (`QuotaConfidence`:
Unknown/Low/Medium/High), `RemainingFraction` (0..1, **NULO = desconhecido**, jamais
inventado), `ResetAt`, `StaleAfter` e `OverrideReason` auditado. `UnknownFrom(...)` é a forma
honesta quando a CLI não publica número confiável; `IsStale(now)` e `IsExhaustedAt(now)` são
puros. Regra: cota `Exhausted` (antes do reset) bloqueia; `Unknown`/stale **não** bloqueia —
aplica-se o limite local conservador (concorrência).

## Scheduler (5, CA-6) — `AgentAccountScheduler`

`Select(registry, request) → AccountSelectionDecision`. A decisão é EXPLICÁVEL: cada conta
vira um `AccountSelectionCandidate(alias, eligible, reasonCode, priority)`; os elegíveis são
ordenados por prioridade decrescente, saudável-antes-de-degradada no empate, e alias
determinístico. O primeiro é o selecionado; os demais elegíveis são os `FallbackAliases`
ordenados. **A preferência nunca promove um inelegível** (fail-closed).

Classificação fechada, na ordem (primeira recusa vence):

| Ordem | Checagem | Razão (recusa) |
|---|---|---|
| 1 | papel lógico permitido | `account.role_not_allowed` |
| 2 | adapter implementado | `account.adapter_not_implemented` |
| 3 | capacidade do executor | `account.capability_unsupported` |
| 4 | estado (Disabled/Unavailable/AuthenticationRequired) | `account.disabled` / `account.executor_unavailable` / `account.authentication_required` |
| 5 | cota esgotada (snapshot ou estado QuotaLimited + cooldown) | `account.quota_limited` |
| 6 | cooldown transitório | `account.cooling_down` |
| 7 | concorrência (`active < limit`) | `account.concurrency_exhausted` |
| 8 | escopo de path permitido | `account.path_scope_not_allowed` |
| 9 | independência actor↔critic | `account.actor_cannot_be_critic` |
| — | elegível (Degraded marcada) | `account.eligible` / `account.eligible_degraded` |

Invariantes da missão, cada uma provada:

- **adapter ausente ⇒ Unavailable** (Kimi sem adapter é recusado, nunca "suportado por
  suposição").
- **instalado sem login ⇒ AuthenticationRequired**.
- **cota esgotada ⇒ QuotaLimited até o reset**; **cota desconhecida NÃO bloqueia** (instalação
  provada; limite conservador na concorrência).
- **saúde ≠ cota**: conta Degraded mas instalada ainda executa (elegível, não preferida a
  igual prioridade).
- **cota ≠ instalação**: quota Unknown com adapter presente é elegível.
- **conta não executa acima da concorrência**.
- **mesma conta nunca é actor e critic** (`ForCritic` + `ActorAlias` igual ⇒ recusa).
- **conflito de escopo de path impede o escalonamento**.
- **preferência não sobrepõe fail-closed**: Antigravity (prio 90) sem login cede ao Codex
  critic (prio 80) elegível.

## Drain / handoff (5.2) e recovery (5.3)

O substrato de fencing já existe e é reutilizado: `AgentAccountRegistry.Reserve/Release`
(fencing crescente, dupla reserva/concorrência/cota recusadas), `AccountProfileProvisioner`
`AcquireLock/RenewLock/ReleaseLock` (concessão do perfil com fencing) e `RecoverStaleLocks`
(recovery após crash: nenhuma conta presa por processo morto). N4 acrescenta o passo de
DECISÃO: ao drenar por cota (`MarkQuotaLimited`), o scheduler re-seleciona o **fallback
tipado** sem duplicar — provado no teste de drain→handoff (Antigravity drenada ⇒ Codex
critic). A montagem do novo bundle/attempt de handoff com provenance é a mesma primitiva
`resumeFromAttemptId` já entregue na cadeia de continuação governada (CA-8).

## Testes

`AgentAccountSchedulerTests` (11, unit): adapter ausente, sem login, cota
esgotada-vs-desconhecida, saúde≠cota, concorrência, actor≠critic, conflito de escopo,
preferência-não-sobrepõe, seleção+fallback ordenado, drain→handoff, papel incompatível.

Regressão verde: UnitTests 277/277, Architecture 7/7, Contract 41/41, build Release
0 warnings.

## Limite honesto

O scheduler decide a seleção; a AQUISIÇÃO real (lease/fencing/claim de path) e a EXECUÇÃO
concorrente de dois actors são exercidas no Piloto 2. O snapshot de cota vem de fora (probe
da CLI ou override do operador); enquanto a CLI não expõe número confiável, o status é
`Unknown` e o limite efetivo é a concorrência local — nunca um percentual inventado.
