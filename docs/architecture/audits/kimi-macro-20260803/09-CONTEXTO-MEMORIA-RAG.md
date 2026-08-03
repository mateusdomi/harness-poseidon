# 09 — Contexto, memória e RAG

> Sem marketing: o que existe, o que é usado e quantos consumidores produtivos tem.

---

## 1. As camadas de memória

| Camada | Onde | Existe | É usada | O que indexa | Volume medido (03/08) |
|---|---|---|---|---|---|
| **Conversa** | `conversations`, `conversation_messages`, `chat_turns` | ✅ | ✅ | mensagens do usuário e da Bruna | ativo |
| **Sessão do modelo** | `SessionId` da CLI (`--resume`) | ✅ | ✅ | **fora do produto** — no fornecedor | — |
| **Estado do chefe** | `chief_states`, `chief_turn_intents`, `chief_turn_blocks`, `chief_causal_edges`, `chief_loop_interruptions` | ✅ | ✅ | decisões, intenções, causalidade | ativo |
| **Notas de contexto** | `chief_context_notes` | ✅ | ⚠️ | notas do chefe | **0 linhas** |
| **Memória de card** | `work_tasks`, `instruction_versions`, `attempt_events`, `work_evidence` | ✅ | ✅ | histórico do trabalho | 294 cards |
| **Snapshots de contexto** | `context_snapshots` | ✅ | ✅ | bundle renderizado + checksum | **777 linhas** |
| **Documentos canônicos** | `governance/manifest.yaml` + arquivos | ✅ | ✅ | documentos declarados | ~2.100 linhas de manifesto |
| **Grafo de código** | `code_graph_nodes`, `code_graph_edges`, `code_graph_snapshots` | ✅ | ⚠️ | símbolos e relações | **0 nós** |
| **Índice vetorial** | `vector_embeddings` (SQLite) / pgvector | ✅ | ⚠️ | documentos | **13 embeddings** |
| **Busca híbrida** | `HybridRagSearchEngine` (FTS + vetor, fusão RRF) | ✅ | ⚠️ | idem | pouco exercitado |
| **Skills** | `skills` | ✅ | ⚠️ | 5 linhas | 5 |
| **Ledger de auditoria** | `audit_ledger` | ✅ | ✅ | tudo | **80.490 eventos** |

---

## 2. Recuperação determinística vs. semântica

O documento de arquitetura afirma que a maior parte do contexto **não** passa por embeddings, e
que isso é escolha de projeto. **A auditoria confirma a afirmação e confirma o número pequeno.**

O `ContextBundleBuilder` é o coração da recuperação determinística, e é bem construído:

```
Build(request)
  1. LoadAndValidate(manifest)         ← documento fora do manifesto reprova
  2. SelectDocuments(manifest, request)← seleção por tarefa, não "carrega tudo"
  3. DetectConflicts(documents)        ← conflito canônico BLOQUEIA o bundle
  4. LoadDocumentSegments + AddRuntimeSegments
  5. ApplyBudget(segments, TokenBudget) → trunca com registro
  6. SecretTextProtector.ContainsSecret(rendered) → bundle com segredo é BLOQUEADO
  7. Sha256(rendered) → cache por checksum, com contagem de hits
```

Pontos fortes reais:
- **orçamento de tokens explícito e truncation registrada** (`truncated`);
- **conflito canônico bloqueia** em vez de escolher um lado em silêncio;
- **varredura de segredo no bundle renderizado** antes de entregar ao modelo;
- **cache por checksum de conteúdo**, com métrica de acerto.

### O RAG semântico

`HybridRagSearchEngine`: FTS + vetor, fundidos por **Reciprocal Rank Fusion** (`0.5/(60+rank)`),
com explicação por documento (`fts_rank:N+vector_rank:M`). É uma implementação correta e
explicável.

**Consumidores produtivos: 3** — `AgentRunOrchestrator`, `ChiefTurnBackgroundService`,
`GovernanceRuntimeEndpoints`. **Corpus: 13 embeddings.** Ou seja: o mecanismo está integrado e
praticamente vazio.

`RagContextProvider.SearchAsync` faz `_vectorIndex.ListAsync(tenant, project)` — **carrega o
corpus inteiro em memória** e ranqueia. Com 13 documentos isso é irrelevante; com 13.000 é um
problema. Achado `F-21` — LOW hoje, MEDIUM quando o corpus crescer.

**Embedding:** `DeterministicLocalEmbedding` — determinístico e local, sem chamada externa.
Escolha coerente com o modo pessoal (sem custo, sem vazamento), com o custo de qualidade
semântica menor que embeddings de modelo.

---

## 3. Contexto do ATOR e do CRÍTICO

### Ator (card comum)

Composição, na ordem em que o `AgentRunOrchestrator` monta:

```
PersonaCardComposer.Compose(persona, card)   ← filosofia, princípios, entregáveis, limites
  + resolution.Card.Scope                     ← o que este card é
  + AcceptanceCriteria
  + instruction_versions[último]              ← inclui achados do crítico numa correção
  + ContextBundle (documentos do manifesto, sob orçamento)
  + RAG slices
  + ChiefReinforcement (quando o despacho é reforço)
  + governança da worktree: CLAUDE.md / AGENTS.md lidos pela própria CLI
```

Base do trabalho de correção: **a branch da tentativa reprovada**, não `HEAD` — preserva o delta
que o crítico mandou corrigir. Detalhe maduro.

### Crítico

```
diff da branch (manager.DiffBranchAsync("HEAD", branch))
  + validação de template documental (pré-review determinística)
  + inspeção de grafo da branch (worktree efêmera governada)
  + prompt: "Você é o revisor independente desta tentativa. Você NÃO implementa e NÃO escreve"
  + --tools <somente leitura>, --permission-mode dontAsk
```

Um detalhe muito bom: **documento inválido não consome um crítico**. A validação de contrato de
template roda **antes** e vira review determinístico, em vez de gastar cota para descobrir depois.

### As duas perguntas do §17

> **O profissional recebe informação suficiente para executar sem redescobrir o projeto?**

**Sim, no caminho documental** — foi o que sustentou 30 cards concluídos nas fases 1–3, com
mediana de ~5,8 min por run.
**Não comprovado no caminho de código**, porque a fase 5 nunca rodou. E o `code_graph_nodes`
está **vazio**, o que significa que a recuperação estrutural de código — o mecanismo desenhado
justamente para "não redescobrir o projeto" — **nunca foi exercitado**.

> **Recebe informação demais e desperdiça contexto?**

Há orçamento e truncation, então não há varredura cega. O sinal de desperdício mede-se por outro
lado: `firstPassAcceptanceRate = 0,33` e `costOfRework = US$ 221 de US$ 303` (**73% do custo é
retrabalho**). Isso é muito. Duas leituras possíveis: contexto insuficiente ou critério do crítico
severo. **Não é possível distinguir com os dados atuais** — não há classificação da causa de
rejeição. Achado `F-22` — MEDIUM, OBSERVABILITY GAP.

---

## 4. Eventos, outbox e ordenação (§30)

| Item | Estado |
|---|---|
| Outbox | `outbox_messages` — **78.363 mensagens**; `outbox_dispatch_failures` para poison |
| Dispatcher | `OutboxDispatcherBackgroundService`, com `OutboxDispatcherOptions.Validate()` |
| Idempotência | Chaves de idempotência em toda mutação da `WorkChain` (`IdempotencyKey`, `IdempotentReplay` como status de primeira classe) |
| Ordenação | Por ULID (monotônico por tempo) — ordenação total dentro do tenant |
| Ledger | `audit_ledger` encadeado, **80.490 eventos**; `LedgerReconciliationBackgroundService` |
| Realtime | `realtime_events`, `realtime_streams` → SignalR |

As transições pedidas no §30 (`Chief→Plan`, `Plan→Cards`, `Card→Attempt`, `Attempt→Review`,
`Review→Phase`, `Phase→Council`) são todas mutações da `WorkChain` com receipt idempotente —
`WorkChainMutationStatus` distingue `Applied` de `IdempotentReplay` de recusa tipada.
**Esta é a parte mais sólida da persistência.**

Ressalva: 78 mil mensagens de outbox contra 80 mil eventos de ledger sugere que o outbox está
sendo usado como log, não como transporte. Vale medir a taxa de entrega e a idade da fila —
não havia consulta pronta para isso. Achado `F-23` — LOW, OBSERVABILITY GAP.

---

## 5. Estado durável — fonte da verdade por conceito (§29)

| Conceito | Fonte da verdade | Cópias/derivados |
|---|---|---|
| Cards, tentativas, reviews, evidências | **SQLite** `~/.harness-poseidon/harness.db` (287 MB) | painel, ledger |
| Fases, objetivos, gates | SQLite (`workflow_*`) | painel |
| Personas | **código C#**, semeado em `agent_definitions` | banco |
| Frota de contas | **`~/.harness/agent-accounts.json`** | `AgentAccountRegistry` em memória |
| Disponibilidade de conta | **`~/.harness/account-availability.json`** | banco/painel |
| Código produzido | **Git** (branch `task/agent-run-<attempt>`) | worktrees |
| Documentos canônicos | **Git** + `governance/manifest.yaml` | `context_snapshots` |
| Auditoria | `audit_ledger` (encadeado) | — |
| Índice semântico | `vector_embeddings` — **derivado, nunca fonte** | — |
| Estado da operação | `coordination/final-operation/STATE.json` (Git) | — |
| Memória do modelo | **fornecedor** (sessão da CLI) | — ⚠️ |

**Nada relevante vive só em memória**, com uma exceção que importa: os dicionários de backoff do
`ChiefBacklogLoopService` (`_reviewBackoff`, `_reviewInfrastructureFailures`,
`_noProgressRuns`, `_dispatchBackoff`, `_reviewerShortageSince`) são **estado em processo** e se
perdem no reinício do Host. Consequência prática: reiniciar o Host **zera o contador de
adiamentos de review** — o que ajuda a recuperar um card preso, mas também apaga o histórico que
justificaria escalar. Achado `F-24` — MEDIUM, ARCHITECTURAL RISK.
