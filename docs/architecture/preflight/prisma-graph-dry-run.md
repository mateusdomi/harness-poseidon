# Prisma — dry-run da ProjectGraphProjection (Onda 4.3)

**Data:** 2026-08-05 · **Projeto:** Prisma (`01KZ7ZNTXRRQMMA9G1TQ2H5BFW`, TrensRJ/GRC, pausado) ·
**Fonte:** `prisma-especificacao-mvp-v3.0.md` (a mesma cópia congelada do preflight) ·
**Prova executável:** `tests/Harness.UnitTests/Graph/PrismaDryRunTests.cs`

## O que o dry-run fez

A especificação real do Prisma foi convertida em grafo ANTES de o projeto rodar, usando o mesmo
secionador que serve os anexos à Bruna (Onda 0.7) e o mesmo projetor da Onda 1:

| Elemento | Nós | Origem |
|---|---|---|
| Requirement | **26** | Um por critério de aceite do §16 (T1..T26), título fiel ao Given/When/Then |
| HumanFact | 1 | O banco corporativo da TrensRJ é Oracle 19c |
| Constraint | 2 | Metodologia travada (🔒 §5) e fórmula/imutabilidade do ITRC (🔒 §6) |

Arestas `constrained_by` (determinísticas, pelo mapa de seções da spec): T1–T8 → metodologia §5;
T9–T15 → ITRC §6. T16–T26 (perfis, aderência, auditoria) não carregam constraint travada.

## Cobertura do §16 hoje

**0/26 critérios cobertos** — nenhum card vivo emite `implements` para critério nenhum, porque o
projeto está pausado e sem cards. Esta é a resposta honesta e é o grafo quem a dá.

O que isto compra para o run: no dia em que a Bruna despachar o primeiro card, a pergunta
"quantos critérios do §16 têm dono?" é respondível por consulta (`GetGateEvidenceGaps` no gate de
testes; cobertura por travessia de `implements`), e o RequirementCoverageAnalyzer deixa de ser a
única linha de defesa — card cancelado não cobre critério, por construção da projeção.

## Propagação verificada no dry-run

- Mudança na **metodologia §5** alcança exatamente os 8 critérios de criticidade/residual
  (T1–T8) — nem um a mais (controle de falso positivo).
- Mudança na **fórmula do ITRC §6** alcança exatamente os 7 critérios T9–T15.
- O **HumanFact Oracle 19c** existe como nó: quando os cards de persistência nascerem
  `constrained_by` ele, uma troca de banco (Eval 1 da missão) propaga para eles e para nada
  além deles.

## Limites declarados

- O dry-run NÃO cria linhas no banco do Prisma: é projeção em memória sobre a spec, congelada
  como teste. A projeção persistida nasce no primeiro `graph rebuild` com a flag ligada.
- O vínculo critério→demanda→card só existirá quando a Fase 1 do run decompor a spec; o dry-run
  fixa o formato e a contagem-alvo (26) que a cobertura terá de fechar.
