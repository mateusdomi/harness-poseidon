# 02 — PESQUISA: HARNESS ENGINEERING E LOOP ENGINEERING
Consolida: 02-LITERATURE-REVIEW, 02-PATTERN-CATALOG, 02-ANTI-PATTERN-CATALOG, 02-CONTEXT-AND-TOKEN-FINDINGS, 02-LOOP-ENGINEERING-FINDINGS. Pesquisa em 19/07/2026. Confiança: A = fonte oficial verificada nesta pesquisa; B = fonte oficial conhecida, verificada indiretamente; C = padrão observado/inferência.

## 1. Fontes primárias verificadas

| Fonte | Organização | Contribuição central | Conf. |
|---|---|---|---|
| Effective harnesses for long-running agents (nov/2025) — anthropic.com/engineering | Anthropic | Padrão initializer + coding agent; progresso incremental por sessão com artefatos duráveis (feature-list JSON, progress log, init script, commit baseline); compaction por sumarização é insuficiente para jobs muito longos — reset de contexto + handoff estruturado | A |
| Harness Design for Long-Running Application Development (mar/2026) | Anthropic | "Todo componente do harness assume que o modelo não consegue algo; essas suposições expiram" — harness deve ser revisado a cada geração de modelo | A (via catálogo verificado) |
| anthropics/cwc-long-running-agents (repo oficial) | Anthropic | **Default-FAIL contract** (todo critério nasce falso; agente só marca aprovado abrindo evidência) e **fresh-context evaluator** (avaliador em contexto limpo, sem ferramentas de escrita) | A |
| Harness engineering: leveraging Codex in an agent-first world; Unrolling the Codex Agent Loop; Run Long-Horizon Tasks with Codex | OpenAI | Harness engineering como disciplina; loop do Codex decomposto; artefatos reutilizáveis Plan.md/Implement.md/Documentation.md para long-horizon | A (títulos e teses verificados via catálogo curado) |
| Claude Code: memória (CLAUDE.md hierárquico, `.claude/rules`), subagents, hooks, Agent Skills (progressive disclosure: metadados sempre carregados, corpo sob demanda), sandboxing | Anthropic docs | Mecânica real de carregamento por provider | B |
| AGENTS.md (padrão aberto, adotado por Codex e dezenas de harnesses) | OpenAI + comunidade | Entrypoint único por diretório, hierárquico, provider-agnóstico de fato | B |
| Multi-Agent Workflows Often Fail... (GitHub Blog, 24/02/2026) | GitHub | Sistemas multiagente = sistemas distribuídos: todo handoff exige schema tipado e validação de fronteira | A (via catálogo) |
| Long-running Agents (Addy Osmani, abr/2026) | — | Log de eventos append-only como espinha da recuperabilidade; budgets e circuit breakers como não-negociáveis de custo | A |
| The Interplay of Harness Design and Post-Training (arXiv 2606.25447); HarnessBridge (2606.12882); Harnesses for Inference-Time Alignment (2605.21516) | Academia | Harness estático acumula informação stale; validação de tool-calls e compaction são heurísticas dominantes; campo em formação | A (abstracts) |

## 2. Respostas às 24 perguntas da missão (síntese; pergunta -> achado -> conf.)

1. **Aderência**: regras curtas, próximas da tarefa, com enforcement mecânico (hook/linter/CI) superam regras longas; contratos Default-FAIL forçam evidência antes de "aprovado" [A]. 2. **CLAUDE/AGENTS sem desperdício**: entrypoint magro (alvo < 150 linhas / < 2k tokens) com ponteiros; nunca enciclopédia [B]. 3. **Progressive disclosure**: padrão Agent Skills — metadado sempre, corpo sob demanda por relevância [B]. 4. **Canônico + adapters**: manter UMA fonte canônica e GERAR CLAUDE.md/AGENTS.md/adapters por script; edição manual só na canônica [C, convergência ECC + prática]. 5. **Precedência/conflito**: declarar cadeia explícita no núcleo (runtime > tarefa > fase > projeto > org > núcleo é para contexto; para NORMAS, núcleo vence); conflito detectado por linter sobre manifest [C]. 6–7. **Bundles e seleção pelo Chief**: seleção determinística por (agente, fase, tipo de tarefa, risco, paths) via manifest; nunca "repo inteiro" [C+A]. 8. **Memória ≠ estado ≠ fonte**: banco autoritativo para estado; markdown de estado é projeção gerada; memória tem procedência e escopo [A/C]. 9. **Aprendizado seguro**: candidato -> revisão -> eval -> promoção com gate; nunca autopromoção [C, consenso]. 10–11. **Medir entrega/uso**: receipt persistido do bundle (ids, checksums, tokens) + detecção de referência na saída; contexto obrigatório verificado por checklist mecânico pré-turno [C]. 12–13. **Tokens/stale/gardening**: tokenEstimate no manifest; reviewDueAt vencido = stale; agente jardineiro roda por evento (merge) e propõe, não aplica [C]. 14–15. **Generator/evaluator + proof gates**: avaliador independente em contexto limpo sem ferramentas de escrita; proof-or-stop: sem evidência, o loop para e escala [A]. 16–17. **Loops/budget**: terminal states explícitos, máximo de iterações, budget de custo/tokens/duração com circuit breaker [A]. 18. **Supervisão de subagentes**: resultados tipados com schema validado (não prosa); watchdog determinístico [A]. 19. **Compaction/reset/handoff**: sumarização não basta em horizonte longo; reset + handoff estruturado versionado [A]. 20. **LSP/DAP/logs**: acoplar diagnósticos e depurador ao loop reduz falso-DONE (evidência empírica omp: ganhos de 2–10x em pass rate por formato de edição/ferramenta) [A]. 21. **Métricas**: pass@k, first-pass success, taxa de rejeição de patch stale, regressão via golden/replay [B]. 22. **Blast radius**: sandbox + claims de escopo + branch por tarefa + merge só por gate (já é o desenho do Poseidon) [A]. 23. **Versionar skills/personas/tools**: como artefatos de primeira classe no Git, com testes de regressão de prompt [B]. 24. **Vários harnesses sem duplicação**: canônico + adapters gerados; ECC demonstra o padrão (e o custo quando cresce demais) [C].

## 3. Catálogo de antipadrões (com onde foi observado)

| Antipadrão | Evidência externa | Presente no Poseidon? |
|---|---|---|
| Entrypoint monolítico / enciclopédia | ECC root com dezenas de md + 278 skills (risco de 2495 docs dos quais poucos são lidos) | Não (o oposto: nenhum entrypoint) |
| Fonte de verdade fora do versionamento | consenso | **SIM — prompts em ~/Downloads (L1)** |
| Regra copiada em vários arquivos / fontes concorrentes | consenso | **SIM — THREAT_MODEL duplicado divergente (L4)** |
| Regra sem teste/enforcement | GitHub blog: fronteiras sem validação | **SIM — convenção de git add em prosa (L5)** |
| Estado manual concorrendo com banco | Anthropic (estado durável fora do prompt) | Parcial — CURRENT_STATE manual, banco ainda local |
| Contexto atrás de links nunca seguidos / repo inteiro no prompt | Skills/progressive disclosure | Variante: **zero links; discovery só por prompt (L3)** |
| Loop sem terminal/budget/verifier; percentual declarado por LLM | cwc Default-FAIL; Osmani | Não — progresso calculado, gates existem |
| Autor avaliando o próprio trabalho | fresh-context evaluator | Parcial — critic existe no produto; no PROCESSO de construção, o próprio Codex declara verde (mitigado por evidência) |
| Memória autopromovida | consenso | Não há memória alguma (L8) |
| Docs gerados editados manualmente | consenso | Risco futuro (nada é gerado hoje) |

## 4. Implicação central para o Poseidon

O Poseidon já implementa NO PRODUTO os padrões de loop (gates, evidência, watchdog, budget, actor-critic). A lacuna está NO PROCESSO DE CONSTRUÇÃO e na CAMADA DOCUMENTAL: sem entrypoints versionados, sem índice, sem canônico+adapters, sem receipts e sem pipeline de aprendizado. A proposta (doc 04) fecha exatamente esse delta, reaproveitando o enforcement mecânico que já existe em `tools/backend/`.
