# INC-EVAL-002 — card crítico não tem persona elegível e a rejeição vaza a claim do workspace

**Data:** 2026-08-06 06:21–09:10 UTC · **Projetos:** Prisma e Indicadores TrensRJ ·
**Detecção:** supervisão hands-off (rejeições `persona_tools_unresolved` + tempestade de
`workspace.scopeconflict` com slot ocioso).

## Sintoma

Desde a entrada na fase 5 (06:15), os FEATs de prioridade `critical` eram rejeitados com
`chief.dispatch_rejected: persona_tools_unresolved`, e cada rejeição deixava viva a claim de
workspace recém-criada (src/**, frontend/**, …). As claims órfãs renovavam-se a cada rodada
mais rápido do que o lease de 15 min vencia, e TODOS os demais cards do projeto colhiam
`workspace.scopeconflict` (150+ rejeições em ~2h30).

## Cadeia causal

1. Projetos criados com criticidade **Crítica** → cards nascem com `priority=critical` e
   `risk_tier=critical`.
2. `ChiefBacklogLoopService.IsEligible` passa **`task.Priority`** como o "risco do card" para
   `PersonaEligibilityPolicy` (mistura de semânticas: prioridade ≠ risco).
3. A política ordena `low<medium<high<critical` e exige `persona.Risk >= risco do card` — mas
   o SCHEMA de `agent_definitions` tem `CHECK (risk IN ('low','medium','high'))`: **nenhuma
   persona pode declarar teto `critical`**. Resultado estrutural: card crítico jamais encontra
   persona elegível (`persona.risk_tier_exceeded`), inclusive o fallback inferido.
4. Persona nula → `RequiredToolIds=null` → o orquestrador nega com `persona_tools_unresolved`
   (fail-closed correto) — mas a negação acontece DEPOIS da claim de path, e o caminho de
   rejeição não libera a claim (só o vencimento do lease libera).

## Correção aplicada (dado, não código — freeze preservado)

`UPDATE work_tasks SET priority='high'` nos 23 cards vivos com `priority='critical'` dos dois
projetos. O `risk_tier` permanece `critical` — os gates de prova continuam os de risco
crítico; só a comparação de elegibilidade (que hoje lê a prioridade) passa a caber no teto
`high` das personas. Reversível. Cards novos nascidos `critical` durante a janela receberão o
mesmo ajuste na supervisão.

Tentativa anterior descartada: elevar o teto das personas para `critical` — bloqueada pelo
CHECK do schema (seria migração, i.e., código).

## Follow-ups para depois da janela

- Corrigir a passagem de `task.Priority` como risco em `IsEligible` (usar `task.RiskTier`).
- Reconciliar schema × política: ou o schema aceita `critical` em persona, ou a política
  trata `high` como teto máximo delegável.
- Liberar a claim de workspace no caminho de rejeição do despacho (hoje só o lease expira),
  eliminando a tempestade de `workspace.scopeconflict` derivada.
- `persona_tools_unresolved` merece aparecer como fato de DoR/atenção em vez de rejeição
  silenciosa repetida (o dono só percebe pela pausa da etapa).

## Resolução em código (2026-08-07, `7df8409d`)

Os dois primeiros follow-ups estão resolvidos juntos: `BoardTaskRecord` ganhou o campo `RiskTier`
(a coluna `work_tasks.risk_tier` já existia, nunca projetada) e os 7 call sites em
`ChiefBacklogLoopService` que liam `task.Priority` passaram a ler `task.RiskTier`.
`PersonaEligibilityPolicy` ganhou um clamp: o teto de comparação de elegibilidade é o maior risco
que uma persona PODE declarar (`high`, já que o schema de `agent_definitions` não aceita
`critical`) — um card `risk_tier=critical` agora cabe numa persona de teto `high`, sem migração de
schema e sem afrouxar os gates de prova de risco crítico, que continuam lendo o `RiskTier` real do
card em outro lugar, não o clamp.

O terceiro follow-up (liberar a ScopeClaim na rejeição pós-acquire) também está resolvido: os 3
pontos de rejeição em `AgentRunOrchestrator.StartAsync` depois do `AcquireAsync`
(`executor.sandbox_unsupported`, `sandbox.unavailable`, `persona_tools_unresolved`/ferramenta
negada — este último é exatamente a tempestade observada aqui) agora chamam `TryFailAsync` +
`TryReleaseWorkspaceAsync` antes de retornar, reaproveitando o par já usado na reconciliação de
órfãos por lease.

O quarto follow-up (`persona_tools_unresolved` como fato de DoR/atenção) segue em aberto.
