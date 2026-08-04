# ADR-0008 — Semântica do Conselho e o fim da recursão de crítico

## Contexto

O Conselho de Agentes é convocado na saída do Planejamento (`4-Planejamento`). Cada assento é uma
lente — Produto, Arquitetura, Tech Lead, e os condicionais Segurança, DBA, DevOps, QA — e cada lente
produz UM parecer.

Mecanicamente, um assento é um card comum: `WorkflowPhaseDriver.CreateCouncilCardAsync` cria um card
com `cardType = "council"`, risco `low`, e o move para `ready`. O agente que o executa escreve o
parecer em `docs/conselho/**` e a tentativa termina em `awaiting_review`.

Aqui estava o impasse. O elo de code review do chefe exige que TODA tentativa em `awaiting_review`
seja revista por uma conta de papel `critic` **diferente** da do ator — a política Actor ≠ Critic.
Só que o parecer do Conselho só pode ser escrito por conta de papel `critic`, porque é ela que tem o
claim de caminho sobre `docs/conselho/**`. O resultado é aritmético, não filosófico:

- cada assento consome UMA conta `critic` como **ator**;
- a revisão daquele assento exige OUTRA conta `critic`;
- com N contas `critic`, o Conselho passa a exigir N ≥ 2 e não paraleliza;
- medido em **2026-08-03**: N = 2, uma delas sem cota. Os seis assentos entregaram o parecer e
  escalaram com `critic.none_available`. A fase 4 não fechou — e, com aquele elenco, não fecharia
  nunca.

O nome disso é recursão de crítico: quem critica passa a precisar de quem o critique, e o custo não
é conceitual, é de elenco.

## Opções

**A — Manter Actor ≠ Critic para o parecer.** Correto no papel e impossível na prática com menos de
duas contas `critic` ociosas por assento. Foi o estado até 2026-08-03, e o efeito observado foi a
fase 4 travada indefinidamente com trabalho concluído e entregue.

**B — Contratar contas `critic` até o Conselho caber.** Resolve por dinheiro um problema de desenho.
E não resolve de verdade: o Conselho passa a escalar com o número de assentos, e assentos crescem
com a complexidade do projeto — exatamente quando a cota está mais disputada.

**C — Desligar a governança para o Conselho** (`if council: skipGovernance()`). Rejeitado sem
hesitação. Um caminho genérico de bypass é a porta pela qual todo o resto sai depois; e o parecer
deixaria de ter recibo, proveniência e autoria — que é justamente o que separa um conselho de um
carimbo.

**D — Reconhecer que o parecer JÁ É a revisão.** O `CouncilOpinion` não é implementação produzida
por um ator: é uma avaliação independente produzida por um assento de governança. Pedir revisão
independente de uma avaliação independente é pedir a mesma coisa duas vezes. O que protege o
Conselho não é revisar cada parecer — é a **consolidação**, que é determinística.

## Decisão

**Adotada a opção D.** Um card `cardType = "council"` (e `"revisao"`, compatibilidade anterior à
migration 0122) recebe veredito de revisão **determinístico**, gravado com alias próprio
(`deterministic-council-gate`) e motivo próprio (`council.policy_accepted`).

Isto **não** é um bypass. É uma via de revisão diferente, declarada, para um tipo de card cuja
natureza é diferente. Concretamente, continuam preservados e persistidos:

| Preservado | Onde |
|---|---|
| assento (lente) | `CouncilOpinion.Seat`, título do card |
| persona | instrução do card, catálogo de personas |
| conta e provedor | `CouncilOpinion.AccountAlias` / `ProviderKind`, medidos do ledger de invocações |
| contexto | `ContextBundle` do turno, com recibo |
| parecer | artefato em `docs/conselho/**`, lido da branch da tentativa |
| bloqueio | `CouncilOpinion.IsBlocking`, estruturado |
| justificativa | `CouncilVerdict.Rationale` |
| dissenso | `CouncilVerdict.Dissent`, inclusive o não-bloqueante |
| recibos | `governance_turn_receipts`, um por turno de assento |
| rodada / ciclo | sufixo de ciclo no título, `MaximumReviewCycles = 3` |
| quórum | `MinimumCouncil = 3`, `MinimumDistinctAccounts = 2` |
| camada determinística | `LayerResult` declarado como `council.policy_accepted` — declarado, não omitido |

## O que protege o Conselho, já que não é a revisão par-a-par

**A consolidação é determinística e estrutural.** `AgentCouncilPolicy.Consolidate` decide a partir de
campos tipados, nunca de prosa:

```
opiniões < MinimumCouncil          → council.incomplete           (não passa)
qualquer IsBlocking == true        → council.blocking_finding     (não passa)
caso contrário                     → council.cleared[_degraded|_diversity_unknown]
```

Três consequências que este ADR existe para fixar:

1. **Um único veredito bloqueante segura a fase.** O Conselho não é votação por maioria: se a lente
   de segurança encontra falha explorável, quatro concordâncias não a tornam menos explorável.
2. **Síntese textual não pode virar veredito.** Nenhum caminho permite que um texto gerado por
   modelo transforme `IsBlocking = true` em `MayProceed = true` — a consolidação nunca lê a prosa
   para decidir, só para registrar.
3. **Ausência nunca é aprovação.** Assento não ouvido produz opinião OPERACIONAL bloqueante:
   segura a fase, e não gera card de correção — porque falha de infraestrutura não é achado técnico,
   e abrir trabalho para "corrigir" uma opinião que ninguém deu foi um defeito real.

## Compatibilidade

- Cards `council` criados antes desta decisão seguem o mesmo caminho — a dispensa é por tipo, não
  por data.
- `cardType = "revisao"` (anterior à migration 0122) continua reconhecido; cards novos nascem como
  `council`.
- Nenhum outro tipo de card herda a dispensa, e o **título** não a concede: um card chamado
  "parecer do conselho" com `cardType = "tarefa"` continua exigindo crítico independente. Coberto por
  `CouncilOpinionReviewExemptionTests`.

## Recuperação

- **Capacidade curta serializa, não bloqueia.** `MaximumConcurrentSeats` limita os assentos abertos
  ao número de contas distintas elegíveis; o que não coube num ciclo não é perdido nem recusado —
  o ciclo seguinte encontra o assento na mesma posição, porque a lista de assentos é estável por
  construção.
- **Reinício no meio retoma.** A convocação procura os cards existentes pelo prefixo estável do
  título antes de criar qualquer coisa; um Host que morra com três assentos abertos volta e encontra
  os três, sem duplicar.
- **Parecer entregue e não colhido é recuperado da branch.** `ICouncilOpinionArtifactReader` lê o
  artefato real da tentativa quando `work_attempts.summary` está vazio — sem ele, um conselho
  inteiro entregue ficava eternamente `council.incomplete`.
- **Ciclo de correção tem teto.** `MaximumReviewCycles = 3`; estourado, escala em vez de girar.

## Relacionado

- `src/Modules/Harness.Modules.Coordination/Application/AgentCouncilPolicy.cs` — consolidação.
- `src/Harness.Host/Agents/ChiefBacklogLoopService.cs` — `IsCouncilOpinionCard`, via determinística.
- `src/Harness.Host/Workflows/WorkflowPhaseDriver.cs` — convocação, assentos, ciclos.
- Commit `a77f79cf` — implementação da decisão, anterior a este registro.
- [ADR-0004](ADR-0004-maturidade-honesta-das-fases-6-9.md) — maturidade declarada das fases.
