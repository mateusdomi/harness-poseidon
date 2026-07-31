# Inventário de arquivos e componentes do cérebro da Bruna

Data de corte: 2026-07-30. Branch: `develop`. Este inventário descreve o código observado; não promove documentação de auditoria a fonte canônica.

## Método e contagens

O censo partiu dos 2.014 arquivos rastreados por Git e combinou: termos de domínio (`Bruna`, `Chief`, `agent`, `conversation`, `demand`, `workflow`, `context`, `memory`, `vector`, `MCP`, `attempt`, `workspace`, `review`, `quota`, `ledger`, `outbox`), referências de composição em `HostApplication`, chamadas de interfaces para implementações SQLite/Postgres e testes dos caminhos encontrados. O conjunto reproduzível contém 1.152 arquivos relevantes ao control plane; 315 mencionam diretamente Bruna/Chief ou seus contratos e 837 influenciam indiretamente esses caminhos.

As contagens abaixo usam estas definições:

- **prompt**: template de mensagem enviado a modelo, não cada persona ou instrução de domínio;
- **agente**: definição canônica semeada no catálogo, não conta contas de provedor;
- **morto/não carregado**: implementação sem consumidor de produção comprovado, ou instrução que não pode ser selecionada com os identificadores usados no runtime;
- **redundante**: duas ou mais representações do mesmo fato operacional.

```text
Quantidade de arquivos que influenciam diretamente a Bruna:
315

Quantidade de arquivos que influenciam indiretamente:
837

Quantidade de prompts identificados:
6 templates principais + 1 fragmento de continuação

Quantidade de definições de agentes:
25 definições canônicas + 9 especialidades de playbook

Quantidade de fontes de verdade concorrentes:
6 conjuntos

Arquivos potencialmente redundantes:
8

Arquivos mortos ou não carregados:
7 componentes/artefatos comprovados
```

Os seis conjuntos concorrentes são: persona da Chief; configuração modelo/conta; estado de disponibilidade/cota; definição de workflow; memória/contexto; e estado Git versus banco na integração.

## Instruções Markdown

Todos os Markdown rastreados antes desta auditoria foram classificados:

| Arquivo(s) | Responsabilidade e consumidor | Momento/escopo | Persistência / SoT | Status | Evidência e risco |
|---|---|---|---|---|---|
| `AGENTS.md` | Adaptador gerado para Codex; descoberta implícita pelo cliente externo, não pelo Host | execução de CLI/repositório | Git; derivado | parcial | `governance/manifest.yaml`, provider `codex`; não aparece no recibo do bundle Poseidon. Médio |
| `CLAUDE.md` | Adaptador gerado para Claude Code | execução de CLI/repositório | Git; derivado | parcial | provider `claude-code`; carregamento é comportamento do CLI, não é registrado pelo Host. Médio |
| `governance/core.md` | núcleo de governança carregado pelo builder e também diretamente pela Chief | inicialização/contexto global | Git; SoT normativa | ativo | `ContextBundleBuilder.cs`; `ConversationChiefAgentExecutor.LoadGovernanceCore`. Baixo |
| `governance/rules/*.md` (12) | autoridade, capacidade, Chief, review, coordenação, custo, documentação, Git, segredos, segurança e testes | bundle condicionado pelo manifesto | Git; SoT normativa | ativo/parcial | seleção em `governance/manifest.yaml`; aplicação depende dos metadados do pedido. Médio |
| `docs/agents/bruna.md` | persona e limites declarados da Bruna | pretendido para Chief | Git; documentação concorrente | não selecionado no fluxo observado | manifesto seleciona `agents: bruna`, mas o bundle recebe ULID da Chief. Alto |
| `docs/architecture/overview.md` | arquitetura conceitual | humano/bundle | Git; alvo, não fato runtime | parcial | contém capacidades mais amplas que as conexões comprovadas. Médio |
| `docs/architecture/workflows/*.md` (4) | intake, estados, paralelismo e workflow-padrão | humano/bundle por workflow | Git | parcial | máquinas de estado existem, mas execução autônoma e recuperação não correspondem integralmente. Alto |
| `docs/backend/context.md` | contrato pretendido de montagem de contexto | humano/bundle | Git | parcial | runtime tem budget/recibos, mas seleção de instruções e replay são incompletos. Alto |
| `docs/backend/memory.md` | memória/RAG pretendidos | humano/bundle | Git | parcial | menciona mecanismos vetoriais não presentes no armazenamento real. Alto |
| `docs/backend/evals.md` | evals pretendidas | humano/bundle | Git | parcial | judge LLM não possui transporte produtivo; avaliação estrutural é rasa. Alto |
| `docs/backend/observability.md` | telemetria | humano/operador | Git | ativo/parcial | OTel e métricas existem; não há reconstrução do prompt/contexto exato. Médio |
| `docs/backend/runbooks/*.md` (2) | incidente e recuperação | operação/humano | Git | parcial | engines duráveis testados não são o workflow central da Chief. Alto |
| `docs/contracts/*.md` (7) | APIs, cards, canais, eventos, handoff, quota, review | runtime/humano | Git; contrato declarativo | ativo/parcial | contratos tipados existem; alguns efeitos operacionais ficam fora da transação. Médio |
| `docs/decisions/ADR-0001-arquitetura-definitiva-v3.md` | arquitetura-alvo | humano | Git | parcialmente implementado | pgvector/sqlite-vec e durabilidade unificada não foram comprovados. Alto |
| `docs/security/*.md` (3) | isolamento, segredos, threat model | humano/bundle | Git | parcial | bons controles de borda; execução Claude não é interceptada por ferramenta. Alto |
| `README.md`, `docs/INDEX.md` | navegação e visão geral | humano | Git | ativo | não são instrução cognitiva direta. Baixo |
| `RELATORIO-VALIDACAO-POSEIDON.md` | relatório histórico allowlisted | humano | Git; não canônico | legado | não deve orientar runtime. Baixo |

Não existe `CODEX.md`. Não há outros `AGENTS.md` ou `CLAUDE.md` rastreados em subdiretórios.

## Inventário do caminho de execução

| Arquivo ou componente | Tipo | Responsabilidade / consumidor | Momento e escopo | Persistência / SoT | Dependências | Status | Evidência / risco |
|---|---|---|---|---|---|---|---|
| `src/Harness.Host/Program.cs` | entry point C# | chama `HostApplication.Build` | boot global | efêmero | Host | ativo | composição real. Baixo |
| `src/Harness.Host/HostApplication.cs` | composition root | registra stores, workers, modelos, contexto, workflow e telemetria | boot global | configuração | todos os módulos | ativo | linhas 208–745. Crítico para leitura do runtime |
| `src/Harness.Launcher/*` | launcher C# | ciclo de vida desktop e raiz controlada | boot/processo | arquivos locais | Host | ativo | isolamento depende do modo. Médio |
| `src/Harness.Host/Conversations/ConversationEndpoints.cs` | API | recebe turno humano, valida projeto e enfileira | sessão/projeto | `conversation_*`, `chief_turns` | turn store | ativo | `StartTurn`, linhas 438–556. Baixo |
| `src/Harness.Host/Workers/ChiefTurnBackgroundService.cs` | worker | adquire turno, monta contexto, chama Chief, persiste e materializa demandas | turno | DB + ledger | executor, RAG, planos | ativo | conclusão ocorre antes da materialização. Crítico |
| `src/Modules/Harness.Modules.Agents/Infrastructure/Conversation/ConversationChiefAgentExecutor.cs` | adaptador LLM | prompt principal, persona embutida, sessão do provedor, parse estruturado | turno | sessão/model invocation | contas externas | ativo | linhas 204–541. Alto |
| `ChiefTurnOutputContract` no mesmo arquivo | schema C# | `response`, demandas, risco e ações de equipe | turno | JSON/DB | parser | ativo | não registra requisito explícito versus inferido. Alto |
| `IChiefTurnStore` + stores SQLite/Postgres | contrato/persistência | fila, lease, fencing, complete/fail | turno | durável | DB | ativo | lease de dois minutos sem renew. Alto |
| `ProjectStatusDigestService` | serviço | resumo de status enviado à Chief | turno/projeto | derivado | projeto/cards | ativo | limitado; não substitui histórico. Médio |
| `FreshContextEvaluator` | avaliador determinístico | valida forma da resposta | turno | recibo/ledger | output contract | ativo, superficial | aceita conteúdo semanticamente fraco se estruturalmente válido. Alto |
| `CommunicationDisciplineValidator` | guardrail | disciplina da comunicação | turno | efêmero | resposta | ativo | não valida correção do plano. Médio |
| `DemandDecompositionPlanner` | planejador determinístico | transforma demanda em slices por heurística | demanda | `demand_plans` | acceptance criteria | ativo | não é planejamento deliberativo. Alto |
| `src/Harness.Host/WorkBoard/DemandPlanMaterializer.cs` | materializador | cria cards do plano | demanda/projeto | DB | board/plan store | ativo | marca materializado antes de criar cards. Crítico |
| `ChiefCardResolver` | roteador | resolve papel/especialidade para card | dispatch | catálogo | agent definitions | ativo | roteamento majoritariamente por regras. Médio |
| `src/Harness.Host/Agents/CanonicalAgentDefinitions.cs` | catálogo/seeder | 25 personas canônicas | boot/tenant | `agent_definitions` | DB | ativo | persona Chief também existe embutida. Alto |
| `src/Harness.Host/Agents/PlaybookSpecialtySeeder.cs` | catálogo/seeder | 9 especialidades | boot/tenant | `team_specialty_catalog` | perfil | ativo | catálogo maior que capacidade executável. Médio |
| `ChiefTeamManager` | serviço | cria papéis dinâmicos pedidos pela Chief | turno/projeto | DB | catálogo | parcial | agentes nascem sem modelo, skills ou tools. Médio |
| `src/Harness.Host/Agents/ChiefBacklogLoopService.cs` | scheduler/supervisor | promove, despacha, coleta, revisa, corrige e integra | processo/projetos | DB + estado em memória | cards/runs/review | ativo somente com `AutoDispatch` | default desabilitado; primeiro tenant/limites/pouca fairness. Alto |
| `AgentRunOrchestrator` | orquestrador | claims, worktree, contexto, CLI, heartbeat e término | tentativa | DB/Git/processo | executors, PEP | ativo condicional | background task local; retomada não continua processo. Alto |
| `AttemptRecoveryBackgroundService` | reconciliador | marca tentativas expiradas e libera recursos | boot/periódico | DB | profiles/workspaces | parcial | processa apenas o primeiro tenant. Alto |
| `ExecutionCheckpointService` | checkpoint | captura/retoma checkpoint lógico | tentativa | DB | checkpoint store | não conectado | sem chamador produtivo. Alto |
| `IDurableExecutionEngine` SQLite/Postgres | engine | comandos, leases, checkpoint, DLQ, outbox | genérico | DB | watchdog | ativo isoladamente | não conduz turnos/runs da Bruna. Médio |
| `IsolatedAttemptOrchestrator` | engine isolado | estágios duráveis em sandbox | tentativa | DB | isolated workers | parcial/feature flag | caminho separado do `AgentRunOrchestrator`; default disabled. Alto |
| `GitWorktreeManager` | Git | worktree/branch/harvest/merge | tentativa/integração | Git/filesystem | agent run | ativo | lock apenas de instância/processo. Alto |
| `AttemptWorkspaceStore` SQLite/Postgres | lease/claims | exclusividade de tentativa e paths | tentativa | DB | fencing | ativo | bom controle declarado; depende da exatidão do scope. Médio |
| `SerializedMergeWorkChainStore` | coordenação | serializa mutações do work-chain | integração | DB + `SemaphoreSlim` | merge service | parcial | não serializa o `git merge` entre processos. Crítico |
| `TaskIntegrationService` | integração | faz merge Git e depois atualiza board | card/projeto | Git + DB | review/workchain | ativo | ausência de atomicidade Git↔DB. Crítico |
| `CriticReviewAgent`/executor | prompt + adaptador | revisão independente por diff | revisão | review store/model invocation | critic account | ativo | sem acesso ao repo; teste não coletado automaticamente. Alto |
| `CodeGraphDerivationService` | análise estática | grafo e diagnóstico C# | review/integration | artefato DB | Roslyn | ativo | cobertura de linguagens/semântica limitada. Médio |
| `WorkflowPhaseDriver` e stores | estado | fases, obrigações, gates e rebaseline | projeto | DB | workflow definitions | ativo | não é um grafo cognitivo unificado. Médio |
| `ContextBundleBuilder` | context engineering | manifesto, precedência, budget, checksum, conflito e recibo | turno/run | snapshot metadata | manifest/catalog | ativo | não persiste conteúdo completo para replay. Alto |
| `ChiefContextComposer` | contexto conversacional | história, resumo e externalização de notas | turno | mensagens/notas | conversation store | feature default off | lê os 200 mais antigos; notas não são recuperadas. Alto |
| `HybridRagAndContextBuilder` | RAG | lexical + cosseno + RRF e montagem | turno/run | `vector_embeddings` | vector index | ativo/parcial | corpus produtivo quase só anexos. Alto |
| `DeterministicLocalEmbedding` | algoritmo | feature hashing de termos em 256 dimensões | index/query | vetor JSON | SHA-256 | ativo | não é embedding semântico treinado. Médio |
| `SqliteVectorIndex`, `PostgresVectorIndex` | storage/busca | JSON + varredura + cosseno em memória | query | DB | embedding artesanal | ativo/parcial | nenhum banco/índice vetorial. Alto |
| `SolicitationAttachmentEndpoints` | ingestão | valida upload e indexa snippet | intake | blob/DB/vector | intake service | ativo | indexa nome + preview, não conteúdo binário. Alto |
| `MultimodalIntakeService` | ingestão | assinatura, MIME, redaction e preview | intake | DB/blob | attachment policy | parcial | não extrai PDF/Word/XLSX/imagem; não faz OCR. Alto |
| `LearningCandidateEndpoints` + store | governança | candidatos, revisão humana e promoção | manual | DB | policy | parcial | não é alimentado nem consumido pela Chief. Médio |
| `ModelRoutingService`/provider catalog | roteamento | escolhe modelo, custo e fallback declarados | turno/run | DB | provider settings | parcial | executor Chief resolve conta em registro separado; fallback não executa. Alto |
| `ExternalAgentAccountFactory` e executores | integração | Claude Code, Codex, GLM, Antigravity | run/review/Chief | processos externos | contas locais | ativo/parcial | Kimi declarado, porém não implementado. Médio |
| `AccountAvailabilityLedger` | quota/liveness | disponibilidade por alias | global host | JSON em home | collector/router | ativo | não tenant-scoped; concorre com DB e estado em memória. Alto |
| `ProviderQuotaCollector` | quota | snapshots, cooldown e Unknown honesto | dispatch | DB/memória | CLIs | parcial | sem quota confiável para vários provedores. Médio |
| `McpServerCatalogEndpoints` + stores/migrações | catálogo MCP | CRUD de servidores | configuração | DB | API | protótipo | nenhum cliente/transporte/negociação MCP encontrado. Médio |
| `SecurityPolicyEnforcementPoint` | policy engine | capability, path/tool, fencing e auditoria | pré-execução | memória + audit | orchestrator | parcial | valida só o binário executor, não tool calls internas. Crítico |
| `PoseidonTelemetry` | OTel | traces, métricas, redaction e export OTLP | global | exporter | Host | ativo | sem prompt/contexto exato nem tool spans internos. Médio |
| ledger/outbox/receipts stores | evidência | trilha append-only e publicação | todas as mutações | DB | stores | ativo | `Detail` de auditoria não é redigido centralmente. Alto |

## Templates de prompt

| Template | Arquivo | Uso | Status |
|---|---|---|---|
| Chief principal | `ConversationChiefAgentExecutor.cs` | interpretação, demandas e team actions | ativo |
| Chief repair | mesmo arquivo | reparar JSON/schema inválido | ativo |
| worker execution | executor/orquestrador de agentes | implementar card em worktree | ativo condicional |
| critic review | executor do crítico | revisar diff e critérios | ativo condicional |
| run-target fallback | `RunTargetAgentFallback.cs` | fallback assistido para alvo de execução | ativo em fluxo específico |
| LLM eval judge | implementação de eval judge | julgar avaliação | inacessível no composition root atual |
| continuation fragment | contexto/recovery do run | orientar continuação após correção | ativo como fragmento, não template independente |

As 25 personas em `CanonicalAgentDefinitions.cs`, as nove especialidades e os documentos do manifesto são conteúdo de contexto, não foram artificialmente contados como dezenas de prompts.

## Componentes mortos, não carregados ou apenas conceituais

1. `ExecutionCheckpointService`: persistência implementada, sem chamada produtiva.
2. Recuperação de `ExternalizedNote`: escrita existe, leitura não é chamada.
3. LLM-as-a-judge: fábrica recebe `transport = null`; caminho produtivo fica indisponível.
4. MCP: catálogo e CRUD sem cliente/runtime MCP.
5. `docs/agents/bruna.md`: não selecionado pelos identificadores enviados ao bundle.
6. Fallback Kimi: conta/modelo declarado, factory explicitamente não implementada.
7. `docs/agents/chief-orchestrator.yaml`: referência em comentário, arquivo inexistente.

## Conclusão do inventário

A densidade de artefatos é alta, mas o “cérebro” efetivo é menor: uma Chief LLM por turno, um decompositor determinístico, um board persistente, um loop local opcional e atores/críticos externos. As maiores divergências surgem onde documentação-alvo, stores duráveis e caminhos de demonstração existem sem estarem conectados ao workflow real.
