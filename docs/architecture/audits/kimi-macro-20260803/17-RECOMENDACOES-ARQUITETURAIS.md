# 17 — Recomendações arquiteturais

> **Nada aqui foi implementado nesta sessão.** A ordem de auditoria pede catalogar, não corrigir.
> As recomendações estão em ordem de retorno sobre esforço.

---

## Prioridade 1 — destravar o Conselho (bloqueia o piloto hoje)

### R-01 · Decidir a política de revisão do parecer

O parecer do Conselho é, por construção, uma opinião crítica sobre trabalho de terceiro.
Exigir que ele passe por revisão independente cria uma regressão que a política não resolveu, e
o custo é estrutural: com N contas critic, o Conselho exige N≥2 e não paraleliza.

**Três opções, com o trade-off de cada uma:**

| Opção | Como | Ganha | Perde |
|---|---|---|---|
| **(a) Isentar cards `council` da revisão por par** 👈 recomendada | `card_type='council'` pula `awaiting_review` e vai direto para `done` | Simples; o Conselho volta a funcionar com 1 conta critic; **a consolidação já é o controle** — um bloqueante segura tudo, e o dissenso fica no ledger | Um parecer ruim não é filtrado por segundo par de olhos |
| **(b) Revisor de parecer pode ser qualquer papel ≠ ator** | `SelectCriticAliases` aceita `backend-specialist` quando o card é `council` | Mantém o segundo par de olhos; usa a frota inteira | Quem revisa não tem o claim `docs/conselho/**` — precisa de escopo read-only, que já existe para o crítico |
| **(c) Exigir ≥3 contas critic** | Configuração | Nada muda no código | Não escala: N assentos em paralelo exigem N+1 contas |

**Recomendação: (a), com (c) como mitigação imediata de configuração.**
Justificativa: a garantia real do Conselho é a **consolidação** (`Consolidate` — um bloqueante
segura tudo, dissenso registrado, mínimo de 3 lentes). A revisão por par do parecer é uma segunda
camada que custa mais do que entrega, e o próprio código já reconhece que "o conselho não decide,
ele critica".

### R-02 · Commitar e provar o conserto de `F-12`

O working tree já tem `ReviewerShortageGrace = 2h`, `CriticRosterHasCandidate()` e
`WorkAttemptIsReplannable` corrigido. **Está fora do controle de versão** — some se a máquina
reiniciar. Rodar `verify.sh`, commitar, publicar e confirmar ao vivo que um card escalado
por falta de revisor volta sozinho.

### R-03 · Configuração imediata (2 minutos, zero código)

```jsonc
// ~/.harness/agent-accounts.json
{ "alias": "worker-claude-secondary",
  "allowedRoles": ["backend-specialist", "frontend-specialist", "critic"] }
```
Eleva o elenco de críticos de 2 para 3 e destrava o Conselho **hoje**.

---

## Prioridade 2 — dar failover à Chief

### R-04 · Um único seletor de conta

Hoje existem três (`ResolveChiefAccount`, `SelectCriticAliases`, `AgentAccountScheduler`).
Só o terceiro é testado, explicável e consulta cota. **Migrar os outros dois para ele.**

```csharp
// em vez de ResolveChiefAccount()
scheduler.Select(registry, new AccountSchedulingRequest {
    Role = AgentRoles.ChiefOrchestrator,
    RequiredCapability = "chat",
    Now = clock.UtcNow,
    Quotas = capacitySignals,
    PreferMostCapable = true,
});
```

Ganho: a Chief passa a respeitar cota, cooldown e concorrência **de graça**, e a recusa vira
reason code explicável em vez de exceção. `SelectCriticAliases` ganha os mesmos filtros e a
mesma explicação.

**Esforço:** ~40 linhas em 2 arquivos. **Retorno: o maior de todos.**

### R-05 · Declarar uma segunda conta de Chief

Configuração pura, depois de R-04. Sem R-04 o failover não acontece (o seletor atual escolhe
por prioridade sem olhar cota).

---

## Prioridade 3 — fechar a taxonomia tipada

### R-06 · Levar `ExternalFailureKind` até quem decide

A refatoração parou na borda. Os 10 classificadores por substring em
`CardCircuitBreakerService` e `ChiefBacklogLoopService` decidem sobre o **nosso próprio** código
interno — que é exatamente o defeito que o comentário do `ExternalFailureKind` descreve como
origem de 1h40 de laço.

Completar com os campos que faltam ao contrato:

```csharp
public sealed record AgentExecutionOutcome(
    ExternalFailureKind Kind,
    FailureOrigin Origin,        // Ours | Provider | Unknown   ← falta
    ExecutionStage Stage,        // Dispatch | Startup | Task | Harvest ← falta
    bool ReachedTaskExecution,   // hoje inferido de ProducedOutput
    bool Retryable,              // hoje derivado
    TimeSpan? RetryAfter);       // hoje em RetryDelayAfterRun
```

**Regra:** `CardCircuitBreakerPolicy` e a decisão de retry do chefe passam a ler **só** o tipo.
Nenhum `Contains` sobrevive em caminho de decisão.

---

## Prioridade 4 — tirar conteúdo de dentro do binário

### R-07 · Persona da Bruna em arquivo

Carregar `docs/agents/bruna.md` (que **já existe, com 136 linhas de especificação rica**) com
fallback para a const atual, e **logar quando o fallback for usado**. Isso resolve `F-04-B`,
deixa o dono ajustar comportamento sem recompilar, e traz para o prompt a especificação de
autonomia em classes A/B/C e a escada antes de escalar — que hoje o modelo nunca vê.

### R-08 · Corrigir o caminho de `governance/core.md` e tornar a degradação ruidosa

```csharp
// hoje: Path.Combine(_options.RepositoryRoot /* = ControlledRoot */, "governance", "core.md")
// deveria: a raiz do repositório do Poseidon (ou do projeto), com log de aviso quando ausente
```
Uma degradação silenciosa de 104 linhas de governança para 6 é o tipo de erro que ninguém
descobre até o comportamento ficar estranho.

### R-09 · Playbook como dado versionado

Mover as 9 fases, gates e documentos para YAML sob `governance/`, semeado com checksum e
validado por schema. Ganho: o dono ajusta processo sem build. Cuidado: manter o gate de schema,
senão o playbook vira campo livre.

---

## Prioridade 5 — o que falta para ser produto, não andaime

### R-10 · Coordenador de recurso pesado

Um semáforo de slots HEAVY (builds, testes, indexação), configurável, com fila e timeout.
**Não confundir com teto de agentes.** Hoje 4 agentes podem disparar 4 builds e a máquina para.
Sem isso, não há instalação em máquina de cliente.

### R-11 · Trazer a supervisão para dentro do produto

`tools/operation/*.sh` e `Harness.OperationSupervisor` são andaime de validação, atados à sessão
de agente que os iniciou — um vigia já morreu com a sessão e o binário ficou defasado por horas.
O que precisa virar produto: watchdog de binário defasado, sonda de tentativa travada
(hoje `probe.sh`), publicação em janela ociosa.

### R-12 · Persistir os contadores de adiamento

`_reviewBackoff`, `_reviewInfrastructureFailures`, `_noProgressRuns`, `_dispatchBackoff`,
`_reviewerShortageSince` vivem em memória. Um reinício apaga o histórico que justificaria
escalar — e o card volta ao começo. Persistir junto com o circuito, que já é derivado e
idempotente.

### R-13 · Ligar `Effort` no despacho autônomo

~15 linhas: `Effort = persona?.DefaultEffort` no `StartAgentRunCommand`, **validado contra
`ExecutorProfile.Capabilities.EffortLevels`** (Codex não aceita `--effort`; Antigravity aceita
até `high`; Claude aceita até `max`). Sem a validação, a flag inválida derruba o run.

---

## Prioridade 6 — observabilidade que muda decisão

### R-14 · Levar a inelegibilidade até o card

`AccountSelectionDecision` já carrega **todos** os candidatos com motivo. Hoje isso morre no log.
Persistir no card e exibir:

```
Este card não foi despachado porque:
  chief-claude-primary      inelegível — papel não permitido
  worker-claude-secondary   inelegível — papel não permitido
  worker-antigravity-review inelegível — é o autor deste trabalho
  worker-codex-critic       inelegível — sem cota até 04/08 01:21
```

Isso é a diferença entre "Precisa de atenção" e um diagnóstico. **É a recomendação de menor
esforço e maior efeito na experiência do dono.**

### R-15 · Classificar a causa da rejeição do crítico

73% do custo é retrabalho e não há como saber se a causa é contexto insuficiente, critério severo
ou enunciado ruim. Um enum fechado no `work_reviews` (`ContextMissing`, `AcceptanceNotMet`,
`ScopeViolation`, `QualityBar`, `Other`) transforma a métrica mais cara do sistema em informação
acionável.

### R-16 · Fechar o buraco de custo da Antigravity

Ela é a única conta critic viva e grava `usage_unknown`/`output_tokens=0`. Todo número de custo
do produto está errado para baixo enquanto isso durar.

---

## Prioridade 7 — travas que evitam repetir defeitos já vistos

### R-17 · Teste que liga assentos do Conselho ao catálogo de personas

`F-13` (o Product Owner virou Software Architect) seria pego por um teste de 5 linhas:

```csharp
[Fact] void TodoAssentoDoConselhoExisteNoCatalogo() =>
    AgentCouncilPolicy.Seats.Select(s => s.PersonaKey)
        .Should().BeSubsetOf(CanonicalAgentDefinitions.All.Select(d => d.Content.Key));
```

### R-18 · Teste que proíbe `Contains` em caminho de decisão

Um teste de arquitetura (a suíte `Harness.ArchitectureTests` já existe) que falhe se
`CardCircuitBreakerService` ou o classificador de retry usarem substring sobre reason code.

### R-19 · Teste que liga `AgentCouncilPolicy.TriggerPhase` ao nome real da fase

Hoje `"4-Planejamento"` é uma string acoplada. Renomear a fase desliga o Conselho **em silêncio**.

---

## O que eu NÃO recomendaria agora

| Não fazer | Por quê |
|---|---|
| Implementar o adapter do Kimi | Custo médio, retorno baixo enquanto Claude e Antigravity atendem. Faz sentido **depois** de R-04, quando o failover for real |
| Religar o contêiner | Cega a frota (Keychain do macOS). A decisão do dono está correta e documentada. Reabrir só com solução de credencial |
| Popular o grafo de código agora | Ele serve à fase 5, que ainda não rodou. Indexar antes de ter código é otimizar o que não existe |
| Investir em RAG vetorial | 13 embeddings, recuperação estruturada atendendo. A afirmação do documento de arquitetura ("não usamos vetor onde uma consulta responde melhor") é defensável e verdadeira |
| Mais uma rodada de "achou bug → corrige → reinicia" | É o ciclo que esta auditoria existe para interromper. As duas correções de Prioridade 1 destravam a prova; o resto é decisão arquitetural, não conserto |
