# 12 — Estado atual do E2E

> Tudo aqui foi lido do banco `~/.harness-poseidon/harness.db` e de
> `coordination/final-operation/`, em 2026-08-03. Nada é declarado.

---

## 1. As duas provas

| Prova | Id | Estado | Motivo |
|---|---|---|---|
| `QA-POSEIDON-E2E-20260802-EMPRESTIMOS` | `01KZ1PPR1XQ1YC7WKQ1NGCBQ81` | **abandonada** | 2 cards de Arquitetura escalados por falhas de cota da conta GLM registradas **antes** das correções, com motivo cru (`executor.exit_code_1`) em vez do canônico. O circuito recalcula do histórico, então eles não voltam sozinhos. Estado semanticamente inválido |
| `QA-PROVA-LIMPA-20260802-EMPRESTIMOS` | `01KZ24JCFRHN2RGP8NHGP75JMK` | **em execução, travada na fase 4** | Conselho sem revisor |

A prova limpa foi **criada pelo produto** (a API que o navegador usa), não por SQL. Preflight
passou sem bloqueadores; a Bruna respondeu em 24 s e fez **uma única pergunta necessária**.

---

## 2. Linha do tempo medida

| Instante (UTC) | Evento |
|---|---|
| 02/08 21:02 | Fase 1 (Triagem) ativa |
| 02/08 21:15 | **Fase 1 completa — 12,6 min** |
| 02/08 21:43 | **Fase 2 (Descoberta) completa — 28,0 min** |
| 03/08 04:00–05:20 | Fase 3 travada e destravada 4 vezes (OPS-033/034/036/037); depois **parada por falta de conta** — cota do GLM é semanal, volta 06/08 |
| 03/08 08:00 | Destravada de verdade: contêiner removido por decisão do dono; primeiras produções reais de token |
| 03/08 11:35 | Laço de recusa do gate documental quebrado (OPS-046) |
| 03/08 13:18 | **Cota tipada provada em produção**: `executor.quota_exhausted` com o instante exato lido do provedor; despacho seguinte para outra conta |
| 03/08 13:28 | A prova voltou a produzir: 101.113 tokens de saída em 209 entradas |
| 03/08 16:16 | **Fase 3 (Arquitetura) completa — 19 cards** |
| 03/08 16:35 | OPS-054 (crítico): a única conta sã de especialista estava fora da eleição há 2h com `account.quota_limited` enquanto o painel a mostrava disponível |
| 03/08 17:37 | Fase 4 fecha os 3 documentos → **Conselho abre 6 assentos → os 6 recusados no mesmo segundo** (OPS-058) |
| 03/08 17:40–17:50 | Correção publicada; **os 6 pareceres são produzidos**, todos por `worker-antigravity-review` |
| 03/08 18:06 | **Os 6 cards escalados**: `critic.none_available` após 4 tentativas |
| 03/08 22:21 | (durante esta auditoria) `worker-codex-critic` → `QuotaLimited` até 04/08 01:21Z — **o Conselho continua sem revisor possível** |

---

## 3. Números medidos

### Fases

```
1  1-Triagem          completed
2  2-Descoberta       completed
3  3-Arquitetura      completed
4  4-Planejamento     active      (3 documentos validated · gate pending)
5  5-Desenvolvimento  pending
6  6-Testes           pending
7  7-Homologação      pending
8  8-Release          pending
9  9-Sustentação      pending
```

### Cards (projeto da prova limpa)

| Estado | Quantidade |
|---|---|
| `completed` / `done` | **30** |
| `escalated` / `blocked` | **6** (os assentos do Conselho) |
| **Total** | 36 |

### Custo e qualidade (`METRICS.json`, medido de `model_invocations`, todos os projetos)

| Métrica | Valor |
|---|---|
| Invocações | 963 |
| Tokens de entrada | 7.646.925 |
| Tokens de saída | 4.652.071 |
| **Custo total** | **US$ 302,96** |
| Custo por artefato aceito | US$ 2,35 |
| **Custo de retrabalho** | **US$ 221,28 — 73% do total** ⚠️ |
| Taxa de aceite de primeira | **33,2%** ⚠️ |
| Desperdício por falha transitória | 0,98% ✅ (meta batida) |
| Duração mediana de run | 347,7 s (~5,8 min) |
| Duração média de run | 5.056,9 s ⚠️ (a média é 14× a mediana — cauda longa de runs travados) |
| Utilização de modelo | 14,8% |

> **Ressalva importante:** estes números **não incluem a Antigravity**, que grava
> `usage_unknown` / `output_tokens=0`. Como ela executou os 6 assentos do Conselho e é a única
> critic viva, o custo real é **maior** do que US$ 302,96, e não se sabe quanto.

### Estado durável global

| Tabela | Linhas |
|---|---|
| `projects` | 27 |
| `work_tasks` | 294 |
| `audit_ledger` | 80.490 |
| `outbox_messages` | 78.363 |
| `context_snapshots` | 777 |
| `vector_embeddings` | **13** |
| `code_graph_nodes` | **0** |
| `chief_context_notes` | **0** |

### Suítes (baseline declarado em `STATE.json`, verde em 03/08 11:25Z)

| Suíte | Testes |
|---|---|
| Unitários | 1.646 |
| Integração | 307 |
| Recuperação | 28 |
| Frontend | 783 |
| **Total** | **2.764** |

> `mandatoryGatesFailed: 0`, `mandatoryTestsPending: 0`.
> Nota: o `02-arquitetura.md` na pasta de apresentação diz "1.682 unitários"; o número medido
> em `STATE.json` é **1.646**. A diferença é pequena, mas o documento de apresentação deve usar
> o número medido.

---

## 4. O que está PROVADO e o que NÃO está

### PROVADO em E2E, com id

- Demanda em linguagem natural → decomposição → cards rastreáveis
- Despacho autônomo com eleição de conta por papel, cota e concorrência
- Execução isolada em worktree com claim de arquivo
- **Revisão por conta distinta** — 29 cards revisados
- Gate documental com contrato de template (seções obrigatórias e ordem vinculante)
- Fechamento de 3 fases inteiras sem clique humano
- **Cota tipada com reeleição automática** (13:18Z)
- **Recuperação de reinício do Host e de morte de worker** sem punir o card
- Convocação automática do Conselho na saída do Planejamento
- Criação de instância de profissional por persona, sob demanda

### NÃO PROVADO

- **Escrita de código de produto pela frota** (fase 5) — o repositório da prova só tem `docs/`
- Testes, homologação, release e sustentação executados
- Merge real de card de código
- Grafo de código populado
- RAG semântico em escala
- Failover da Chief
- Conselho concluído com veredito consolidado
- Multiprojeto com contenção de recursos
- Qualquer coisa com contêiner ligado

---

## 5. A pergunta honesta: por que a prova não avança?

A resposta não é "faltam correções". É esta:

```
fase 4 → fase 5 exige gate aprovado
gate da fase 4 exige Conselho concluído
Conselho exige ≥3 pareceres com revisão independente
revisão independente exige conta critic ≠ produtora
o produtor foi a ÚNICA conta critic disponível
a outra conta critic (codex) está sem cota
                              ↓
                    IMPASSE ESTRUTURAL
```

Não é bug de correção rápida: é **elenco insuficiente + uma regra que consome o próprio elenco**.
Ver [08-CONSELHO-DE-AGENTES](08-CONSELHO-DE-AGENTES.md) e
[17-RECOMENDACOES-ARQUITETURAIS](17-RECOMENDACOES-ARQUITETURAIS.md).
