# ADR-022 — Persona do agente especializado vs. card de demanda

- Status: aceito
- Data: 2026-07-22

## Contexto

O chefe delega trabalho aos outros agentes criando um CARD no board — a unidade AUDITÁVEL de
delegação: o operador vê exatamente o que o agente recebeu e ajusta o modelo para melhorar o
resultado ("card X → resultado Y"). Ao mesmo tempo, o sistema já modela agentes ESPECIALIZADOS
(catálogo `agent_definitions`), como uma empresa: as seis definições iniciais são
`chief-orchestrator`, `product-requirements-analyst`, `software-architect`, `software-engineer`,
`critic-qa`, `technical-writer`, cada uma com `persona`, `mission`, `deliverables`,
`quality_criteria`, `operating_principles`, `communication_style` e `limitations`.

A pergunta: a persona vai no card, ou o chefe avalia o card e chama o agente especializado?

## Decisão

**A persona NÃO é reescrita no card — ela vive no catálogo de agentes.** Uma demanda vai ao
PROFISSIONAL CERTO (a persona especializada), nunca a um generalista que aprende o papel na
hora.

- **O card é a DEMANDA**: título, escopo, **critérios de aceite**, paths autorizados, risco, e
  QUAL persona/especialidade é requerida. É o que o operador audita e ajusta no board.
- **O chefe, ao planejar, escolhe a persona** (a `AgentDefinition`) adequada à demanda e a
  despacha (política em `ChiefBacklogPolicy`), respeitando cota/concorrência/papel.
- **O briefing entregue ao agente = PERSONA (catálogo) + CARD (demanda)**, composto por
  `PersonaCardComposer` (puro/determinístico). São DOIS pontos de ajuste distintos e
  auditáveis: a persona molda o comportamento do profissional (reusável entre demandas); o
  card molda a demanda específica.

### Dois eixos, não confundir

- **Persona/especialidade** (`agent_definitions`: `software-architect`, `technical-writer`, …)
  = quem faz e como.
- **Papel de escopo/claim** (`AgentPathScopePolicy`/`AgentRoles`: frontend/backend/critic) =
  onde pode escrever + qual conta/executor. Derivado dos paths da demanda, não da persona.

A conta que executa é escolhida pela disponibilidade (ledger de cota) + o papel de escopo; a
persona vem do card. Trocar o executor (Claude/Codex/GLM) não muda a persona nem o escopo.

## Consequências

- O operador itera a PERSONA no catálogo (comportamento do profissional) e o CARD no board (a
  demanda) de forma independente — cada um versionado e auditável.
- O prompt do worker deixa de ser "instrução crua" e passa a ser "persona especializada +
  demanda com critérios de aceite", aproximando o sistema do modelo de uma empresa.
- Próximo: o `ChiefBacklogLoopService` lê os cards `Ready`, escolhe a persona/conta pela
  política e delega o briefing composto; o card espelha o resultado (`Running` →
  `AwaitingReview` → `Completed`).
