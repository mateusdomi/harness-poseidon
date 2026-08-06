# INC-EVAL-004 — a decisão do dono replaneja o card, mas ele re-escala no mesmo minuto

**Data:** 2026-08-06 17:59 UTC · **Projeto:** Indicadores TrensRJ ·
**Card:** `01KZAVEASRRB5GDDDPR2VSTC9B` (FEAT/T01 Backend: RBAC configurável) ·
**Detecção:** supervisão hands-off, ao conferir se a decisão humana tinha efeito real.

## Sintoma

O card escalou por esgotamento de rodadas e abriu pedido de atenção humana. A decisão foi dada
pelo caminho oficial (mensagem no chat do projeto). O ledger mostra a decisão APLICADA —
`ReplanEscalatedTaskAsync` gravou `state='ready'`, limpou `blocked_reason`, zerou o breaker — e
o log confirma `decisão do dono replanejou o card (Applied)`. **Dezenas de segundos depois**, na
mesma rodada do laço:

```
Chief: card 01KZAVEASRRB5GDDDPR2VSTC9B ESGOTOU o orçamento de rodadas
(4/4, effort.critical_maximum) — escalado em vez de redespachado.
```

O card voltou a `escalated`/`blocked` sem nunca ser despachado. Do ponto de vista do dono, ele
respondeu e nada aconteceu — o pedido de atenção fica `answered`, o trabalho fica parado.

## Causa

`ReplanEscalatedTaskAsync` devolve o card à fila mas **não toca no orçamento de rodadas**, que é
derivado do histórico por `CountSpentRounds(attemptHistory)` (conta toda tentativa que não seja
`queued`/`cancelled`). Como o histórico é imutável e correto, a contagem continua ≥ `MaxRounds`
e a guarda re-escala na primeira avaliação seguinte. As duas regras estão certas isoladamente;
juntas, tornam a decisão do dono um no-op — não existe caminho pelo qual "aprovar outra
abordagem" volte a executar.

Observação correlata: das 16 tentativas do card, 10 morreram por infraestrutura
(`workspace.scopeconflict`, `persona_tools_unresolved`, cota) e corretamente NÃO consumiram
rodada — a contagem de 4/4 vem de execuções reais reprovadas em review. A guarda anti-laço
estava certa; o que falta é a decisão humana poder reabrir o orçamento.

## Contorno aplicado (sem código — freeze preservado)

Segunda decisão pelo chat: **descartar o card e fatiá-lo** em quatro cards menores (persistência
Oracle, domínio de autorização, aplicação, endpoints+contrato), cada um com orçamento próprio,
mantendo o escopo funcional integral e carregando os achados dos revisores. O fatiamento é a
única saída que a plataforma oferece hoje para um card com orçamento esgotado.

## Follow-up para depois da janela

- `ReplanEscalatedTaskAsync` precisa registrar um marco de replanejamento que a contagem de
  rodadas respeite (contar rodadas DESDE o último replan aprovado pelo humano), senão a resposta
  do dono nunca reabre trabalho.
- O pedido de atenção não deveria ir para `answered` quando a ação decorrente não teve efeito:
  ou reabre, ou informa ao dono que a decisão não pôde ser aplicada.
