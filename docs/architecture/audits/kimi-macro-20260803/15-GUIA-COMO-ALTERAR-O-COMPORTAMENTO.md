# 15 — Guia: "quero mudar X, onde eu mexo?"

> Caminhos reais, verificados nesta auditoria. Todos relativos à raiz do repositório
> (`/Users/mateus/Documents/harness-poseidon-backend`), exceto os marcados `~/`.
>
> **Legenda de custo:**
> 🟢 configuração (editar arquivo, reiniciar) · 🟡 código simples (1-2 arquivos) ·
> 🔴 código estrutural · ⚠️ exige recompilar e **republicar o binário**

---

## Índice rápido

| Quero mudar… | Custo | Onde |
|---|---|---|
| [Personalidade da Bruna](#1) | 🟡⚠️ | `ConversationChiefAgentExecutor.cs:672` |
| [Bruna mais/menos técnica](#2) | 🟡⚠️ | mesma const + `ChiefCommunicationPolicy` |
| [Quando ela pergunta ao humano](#3) | 🟢 | modo do projeto (banco) |
| [Modelo da Bruna](#4) | 🔴 | não existe caminho — precisa ser criado |
| [Trocar Claude por Kimi na Chief](#5) | 🔴 | adapter inexistente |
| [Quem é o actor](#6) | 🟢 | `~/.harness/agent-accounts.json` |
| [Quem é o critic](#7) | 🟢 | `~/.harness/agent-accounts.json` |
| [Nível de reasoning/effort](#8) | 🔴 | não chega à CLI hoje |
| [Concorrência](#9) | 🟢 | `~/.harness/poseidon.env` + accounts |
| [Política de cota/fallback](#10) | 🟡⚠️ | `AgentAccountScheduler.cs` |
| [Playbook](#11) | 🟡⚠️ | `CanonicalWorkflowTemplates.cs` |
| [Gate de uma fase](#12) | 🟡⚠️ | mesmo arquivo + `PhaseGatePolicy.cs` |
| [Adicionar persona](#13) | 🟡⚠️ ou 🟢 | `CanonicalAgentDefinitions.cs` ou API |
| [Adicionar provider](#14) | 🔴 | 3 arquivos |
| [Conselho](#15) | 🟡⚠️ | `AgentCouncilPolicy.cs` |
| [Contexto do agente](#16) | 🟡⚠️ | `ContextBundleBuilder` + manifesto |
| [Ligar/desligar contenção](#17) | 🟢 | `~/.harness/poseidon.env` |

---

<a name="1"></a>
## 1. Personalidade da Bruna

| | |
|---|---|
| **Arquivo** | `src/Modules/Harness.Modules.Agents/Infrastructure/Conversation/ConversationChiefAgentExecutor.cs`, linha **672**, `private const string ChiefPersona` |
| **Configuração** | nenhuma — é constante compilada |
| **Consumidor** | `BuildPrompt()` (linha 222), primeiro bloco do prompt |
| **Efeito** | muda o *system prompt* de todo turno de conversa |
| **Risco** | **Médio.** Se você enfraquecer "Você NÃO implementa código… e NÃO aprova os próprios gates", a Bruna pode tentar agir fora do papel. Os gates continuam segurando, mas o comportamento fica ruidoso |
| **Teste que protege** | `tests/Harness.UnitTests/Agents/*` (contrato de saída). **Não há teste do conteúdo da persona** |
| **⚠️ Atenção** | `docs/agents/bruna.md` **não é lido**. Editá-lo não muda nada. É o achado `F-04-B` |

**Recomendação:** promover a persona a arquivo versionado (`docs/agents/bruna.md`) e passar a
carregá-lo, com fallback para a const. Assim o dono edita comportamento sem recompilar.

---

<a name="2"></a>
## 2. Bruna mais ou menos técnica

Dois lugares, e é importante saber a diferença:

| Quero | Onde |
|---|---|
| Mudar **quem ela é** (profundidade, princípios) | `ChiefPersona` — §1 |
| Mudar **como ela fala** (vocabulário, formalidade, tamanho) | `src/Modules/Harness.Modules.Agents/.../ChiefCommunicationPolicy.cs` → `BuildInstructions(contexto, instruções)` |

`ChiefCommunicationPolicy.Business` é o contexto padrão. A camada de comunicação **altera somente
a forma** — está escrito no próprio prompt que ela não muda papel, governança, escopo, gates nem
regras de execução. **Este é o lugar certo para "menos jargão" ou "mais técnica".**

**Risco:** baixo. **Teste:** `tests/Harness.UnitTests/Agents/ChiefCommunicationPolicyTests` (existe).

---

<a name="3"></a>
## 3. Quando ela pergunta ao humano

| | |
|---|---|
| **Onde** | Modo de autonomia **por projeto**, na tabela `projects` (banco). Alterável pela UI/API do projeto |
| **Valores** | `Autonomous` (a chefe decide o portão por evidência) · `SemiAutonomous` (só os portões escolhidos esperam) · `Manual` (todo portão espera) |
| **Consumidor** | `src/Modules/Harness.Modules.Workflows/Application/PhaseGatePolicy.cs` |
| **Efeito** | decide se um gate pronto vira `ChiefApproves` ou `AwaitHuman` |
| **Regra que NÃO muda** | `Default-FAIL`: sem evidência, reprova nos **três** modos |
| **Risco** | baixo — valor desconhecido cai em `Manual`, o mais conservador |
| **Teste** | `tests/Harness.UnitTests/Workflows/PhaseGatePolicyTests` |

**Fases 7 e 8 são HITL por construção do playbook**, independentemente do modo — o texto do gate
diz "aprovado pelo humano (HITL obrigatório)". Para mudar isso, §12.

---

<a name="4"></a>
## 4. Trocar o modelo da Bruna

**Hoje não existe caminho.** Verificado linha a linha:

```
ConversationChiefAgentExecutor.RunAsync → ExternalAgentRunRequest sem Model
ClaudeCodeExternalAgentExecutor:153     if (request.Model is {Length:>0})  → falso
                                        → a CLI é chamada SEM --model
```

**Para criar o caminho (🟡, ~30 linhas):**

1. adicionar `Model`/`Effort` a `ConversationChiefExecutorOptions` (ou ler de
   `agent_definitions` onde `agent_key='chief-orchestrator'`);
2. propagar para `ExternalAgentRunRequest` em `RunAsync`;
3. `ClaudeCodeExternalAgentExecutor` já sabe emitir `--model` e `--effort`.

**Enquanto isso não existir:** o modelo da Bruna é o **default do binário `claude` instalado**.
Para trocá-lo hoje, a única via é a configuração da própria CLI (fora do Poseidon).

---

<a name="5"></a>
## 5. Trocar Claude por Kimi para a Chief

**Não é possível hoje. Três bloqueios, em ordem de custo:**

| # | Bloqueio | Arquivo | Custo |
|---|---|---|---|
| 1 | Adapter `kimi-code` não existe | `src/Modules/Harness.Modules.Agents/Application/Execution/External/ExternalAgentExecutorFactory.cs` — `IsImplemented` e `Create` | 🔴 ~200 linhas, espelhando `CodexExternalAgentExecutor` |
| 2 | Kimi não declara capability `review` | `ExecutorCatalog.cs` | 🟢 1 linha (se for verdade) |
| 3 | Kimi não tem o papel | `~/.harness/agent-accounts.json` → `allowedRoles: ["chief-orchestrator"]` | 🟢 |

**O que já está pronto e é mérito do desenho:** persona, governança, política de comunicação e
contrato de saída são texto/schema, independentes de provedor. O escopo de path pertence ao
**papel**, não ao provider. Feito o adapter, **trocar a Chief é edição de JSON + restart.**

**Risco depois de feito:** o contrato de saída JSON da Bruna é estrito. Se a CLI do Kimi errar o
schema com frequência, o turno degrada para `unmatched` (responde, não age). Medir antes de adotar.

### Failover Claude → Claude (isto SIM é possível hoje, 🟢 2 minutos)

```jsonc
// ~/.harness/agent-accounts.json
{
  "alias": "worker-claude-secondary",
  "allowedRoles": ["backend-specialist", "frontend-specialist", "chief-orchestrator"]
  //                                       ↑ adicionar isto
}
```
Reiniciar o Host. **Mas atenção:** `ResolveChiefAccount()` ordena por prioridade e **não consulta
cota**. Com prioridades iguais (100 e 100), o desempate é por alias ordinal e
`chief-claude-primary` vence sempre. Para failover de verdade é preciso o §10.

---

<a name="6"></a>
## 6. Mudar quem é o actor

| | |
|---|---|
| **Arquivo** | `~/.harness/agent-accounts.json` |
| **Campo** | `allowedRoles`: `"backend-specialist"` e/ou `"frontend-specialist"` |
| **Consumidor** | `AgentAccountScheduler`, filtro 1 |
| **Efeito** | a conta passa a concorrer aos cards daquele papel |
| **Restart** | **sim** — o registro é carregado no boot |
| **Risco** | o papel carrega o **escopo de arquivos**. Dar `backend-specialist` a uma conta dá acesso a `src/**`, `tests/**`, `infra/**` |
| **Teste** | `tests/Harness.UnitTests/Agents/AgentAccountSchedulerTests` |

Para restringir mais que o papel: preencher `allowedPathScopes` explicitamente (lista não vazia
**vence** o padrão do papel).

---

<a name="7"></a>
## 7. Mudar quem é o critic

Idem §6, com `"critic"` em `allowedRoles`. **Mas leia isto antes:**

- o papel `critic` recebe o escopo `docs/conselho/**` e **nada mais** — ele não alcança `src/`
  nem os documentos que revisa (é o que preserva a independência);
- a revisão exige conta **diferente** do produtor;
- **com 2 contas critic, o Conselho é impossível quando uma delas para** (achado `F-03`).

**Recomendação operacional imediata (🟢):** adicionar `"critic"` a `worker-claude-secondary`.
Isso eleva o elenco de críticos de 2 para 3 e destrava o Conselho hoje.
Efeito colateral a considerar: essa conta passa a poder revisar o trabalho que outras contas
Claude produziram — o que é **permitido** pela regra (a independência é por alias), mas reduz a
diversidade de provedor na revisão.

---

<a name="8"></a>
## 8. Nível de reasoning / effort

**Hoje o effort não chega à CLI em nenhum run autônomo.** Ver `F-05`.

| Camada | O que declara | É aplicado? |
|---|---|---|
| `agent_definitions.default_effort` | `high` / `medium` | ❌ |
| `agents.effort` | `medium` | ❌ |
| `provider_models.effort_mappings_json` | mapeamento por provider | ❌ |
| `AgentRunEndpoints` (HTTP manual) | corpo da requisição | ✅ **único caminho que funciona** |

**Para fazer funcionar (🟡, ~15 linhas):** em
`src/Harness.Host/Agents/ChiefBacklogLoopService.cs`, método `LaunchAsync` (linha 4084),
adicionar `Effort = persona?.DefaultEffort` ao `StartAgentRunCommand`. O orquestrador já propaga
(`AgentRunOrchestrator.cs:1388`) e os adaptadores já emitem a flag.

**Risco:** Codex não suporta `--effort` (`SupportsEffort: false`); o adapter precisa ignorar em
vez de passar flag inválida. Antigravity aceita `low|medium|high`; Claude aceita até `max`.
**Validar contra `ExecutorProfile.Capabilities.EffortLevels` antes de emitir.**

---

<a name="9"></a>
## 9. Concorrência

| Quero limitar | Onde | Valor atual |
|---|---|---|
| Tentativas simultâneas no total | `~/.harness/poseidon.env` → `Harness__AgentRuns__AutoDispatchMaxConcurrent` | **4** |
| Frequência do ciclo | `Harness__AgentRuns__AutoDispatchInterval` | 10 s |
| Por conta | `~/.harness/agent-accounts.json` → `concurrencyLimit` | 1–3 |
| Timeout de run | `Harness__AgentRuns__RunTimeout` | 30 min |
| Duração do lease | `Harness__AgentRuns__LeaseDuration` | 15 min |
| **Por provider** | ❌ não existe | — |
| **Builds pesados (HEAVY)** | ❌ **não existe** | — |

**Aviso operacional:** aumentar `AutoDispatchMaxConcurrent` acima de ~4 nesta máquina é
arriscado — cada agente pode disparar `dotnet build` sem coordenação (`F-20`). O limite de
agentes **não é** limite de recurso.

---

<a name="10"></a>
## 10. Política de cota / fallback

| | |
|---|---|
| **Arquivo principal** | `src/Modules/Harness.Modules.Agents/Application/Accounts/AgentAccountScheduler.cs` — os 10 filtros, em ordem |
| **Cota do fornecedor** | `.../Execution/External/ClaudeCodeExternalAgentExecutor.cs` e `CodexExternalAgentExecutor.cs` — é onde a **frase** do provedor vira `ExternalFailureKind.QuotaExhausted` e o instante de reset é lido |
| **Ledger de disponibilidade** | `~/.harness/account-availability.json` + `AccountAvailabilityLedger` |
| **Recuperação** | `src/Harness.Host/Agents/AccountRecoveryBackgroundService.cs` — intervalo **1 min** |
| **Preferência entre elegíveis** | `PreferenceRankOf()` no scheduler |
| **Assimetria de modelo** | `AccountSchedulingRequest.PreferMostCapable` |
| **Teste** | `tests/Harness.UnitTests/Agents/AgentAccountSchedulerTests` |

**Para dar failover real à Chief (🟡, ~10 linhas):** substituir
`ConversationChiefAgentExecutor.ResolveChiefAccount()` (linha 207) por uma chamada ao
`AgentAccountScheduler.Select()` com `Role="chief-orchestrator"`, `RequiredCapability="chat"`.
Passa a respeitar cota, cooldown e concorrência **de graça**, com reason code explicável.

**Para mudar a política de revisor ausente:** `ChiefBacklogLoopService.cs` —
`MaximumReviewInfrastructureFailures` (4), `ReviewRetryBackoff` (5 min) e, no working tree não
commitado, `ReviewerShortageGrace` (2 h).

---

<a name="11"></a>
## 11. Playbook

| | |
|---|---|
| **Arquivo** | `src/Harness.Host/Workflows/CanonicalWorkflowTemplates.cs`, método `PlaybookStandard()` (linha 150) |
| **O que editar** | os dicionários `phases`, `gates`, `documents`, `transitions` |
| **Consumidores reais** | `WorkflowTemplateSeeder` → banco → `WorkflowPhaseDriver`, `PhaseGatePolicy`, `PhaseObligationPlanner`, `ExecutivePhaseGuard`, `AgentCouncilPolicy.TriggerPhase` |
| **Como chega ao runtime** | `WorkflowTemplateSeedHostedService` semeia no boot, **idempotente**, por nova versão (o nome é estável de propósito) |
| **Restart** | **sim, e recompilar** |
| **Risco** | **Alto.** `AgentCouncilPolicy.TriggerPhase = "4-Planejamento"` é uma **string acoplada ao nome da fase**. Renomear a fase 4 desliga o Conselho em silêncio |
| **Teste** | `tests/Harness.UnitTests/Workflows/*` |

**Existem 7 outros templates** no mesmo arquivo (greenfield, bug, mudança, refactor, documentação,
entrega padrão, entrega técnica de 15 fases). O projeto escolhe o template no vínculo
(`ProjectWorkflowLinker`).

---

<a name="12"></a>
## 12. Gate de uma fase

Duas coisas diferentes:

| Quero mudar | Onde |
|---|---|
| **O critério escrito** do gate (o texto que o agente lê) | `CanonicalWorkflowTemplates.cs`, dicionário `gates` |
| **Quando o gate aprova** (a mecânica) | `src/Modules/Harness.Modules.Workflows/Application/PhaseGatePolicy.cs` |
| **O que conta como evidência** | `PhaseGateEvidence` (mesmo arquivo) + `PhaseObligationPlanner.cs` |
| **Tornar uma fase HITL ou não** | o texto do gate + o modo do projeto |
| **Documentos obrigatórios** | `CanonicalWorkflowTemplates.cs`, dicionário `documents` |
| **Contrato de template de documento** | `ApprovedDocumentCatalogPublisher.ValidateTemplateContract` + `workflow_document_templates` (banco) |

**Risco:** afrouxar `PhaseGateEvidence` quebra a garantia central do produto ("o modelo não
declara pronto"). Qualquer mudança aqui merece revisão humana.

---

<a name="13"></a>
## 13. Adicionar persona

**Dois caminhos, e eles divergem:**

| Caminho | Como | Persiste em migração? |
|---|---|---|
| 🟡 **Canônico** | `src/Harness.Host/Agents/CanonicalAgentDefinitions.cs` + recompilar | ✅ semeado sempre |
| 🟢 **Runtime** | `POST` em `AgentEndpoints` (CRUD de `agent_definitions`) | ⚠️ pode divergir do código |

Uma persona precisa declarar: chave, nome, papel, especialidade, filosofia, missão, princípios,
entregáveis, critérios de qualidade, estilo, **limites**, tags, `default_effort`, time,
`actor_critic`, risco, `AllowedScopes`, `DeniedScopes`, `ActivationCriteria`,
**`NonActivationCriteria`**.

**⚠️ Armadilha real, já materializada:** se você referenciar a persona por chave em outro lugar
(por exemplo num assento do Conselho), **a chave precisa bater exatamente**. `AgentCouncilPolicy`
usa `"playbook-po"` e o catálogo tem `"playbook-product-owner"` → o assento de Product Owner foi
ocupado por um Software Architect (achado `F-13`). **Não há teste que ligue as duas listas.**

---

<a name="14"></a>
## 14. Adicionar provider

🔴 Três arquivos, nesta ordem:

1. `src/Modules/Harness.Modules.Agents/Application/Accounts/ExecutorCatalog.cs` — o perfil:
   comando, flags não-interativas, variável de config home, variáveis de ambiente permitidas,
   `CapabilitySet` (capabilities, streaming, resume, effort e níveis aceitos);
2. `.../Execution/External/<Nome>ExternalAgentExecutor.cs` — o adapter: montar argumentos, ler a
   saída, e **declarar `ExternalFailureKind`** reconhecendo as frases daquele fornecedor;
3. `.../Execution/External/ExternalAgentExecutorFactory.cs` — `IsImplemented` **e** `Create`.

Depois: `~/.harness/agent-accounts.json` com alias, `providerKind`, `executorId`, `credentialRef`
(`keychain://poseidon/<alias>`), papéis, concorrência, prioridade.

**Não pule o passo 2 da taxonomia.** Sem `ExternalFailureKind`, o provider cai na heurística de
substring legada — foi o que aconteceu com Antigravity (`F-10`).

---

<a name="15"></a>
## 15. Conselho de Agentes

| Quero mudar | Onde (`src/Modules/Harness.Modules.Coordination/Application/AgentCouncilPolicy.cs`) |
|---|---|
| Em que fase é convocado | `TriggerPhase = "4-Planejamento"` ⚠️ string acoplada |
| Assentos obrigatórios | `CoreSeats` |
| Assentos condicionais e seus gatilhos | `ConditionalSeats` (cada um com um predicado sobre `CouncilContext`) |
| Piso de conselheiros | `MinimumCouncil = 3` |
| Ciclos de revisão | `MaximumReviewCycles = 3` |
| Regra de consolidação | `Consolidate()` — hoje **um** bloqueante segura tudo |
| Como um parecer é lido | `FromExecution()` — procura a linha `VEREDITO:` com `BLOQUEAR`/`RESSALVA` |

**Risco alto:** baixar `MinimumCouncil` para 1 ou 2 destrói a razão de existir (divergência
isolada precisa aparecer como divergência, não como maioria). Mexer em `Consolidate` para exigir
maioria troca *"evidência decide risco"* por *"maioria decide risco"* — o comentário do código
explica por que isso é errado.

---

<a name="16"></a>
## 16. Contexto do agente

| Quero mudar | Onde |
|---|---|
| Quais documentos entram | `governance/manifest.yaml` (documento fora dele **reprova a verificação**) |
| Como são selecionados por tarefa | `src/Modules/Harness.Modules.Governance/Context/ContextBundleBuilder.cs` → `SelectDocuments` |
| Orçamento de tokens | `ContextBundleRequest.TokenBudget` → `ApplyBudget` |
| Estratégia | `Context/ContextStrategy.cs` |
| RAG semântico | `Memory/HybridRagAndContextBuilder.cs` (FTS + vetor, fusão RRF) |
| Embedding | `Memory/DeterministicLocalEmbedding.cs` |
| Briefing da persona no card | `PersonaCardComposer.Compose` |
| Prompt do crítico | `src/Harness.Host/Agents/AgentRunOrchestrator.cs:1105` |
| Reforço da chefe no despacho | `chiefReinforcement` em `LaunchAsync` |

---

<a name="17"></a>
## 17. Ligar/desligar contenção (contêiner)

| | |
|---|---|
| **Arquivo** | `~/.harness/poseidon.env` (vence) ou `src/Harness.Host/appsettings.json` |
| **Chaves** | `Harness__IsolatedExecution__Mode`, `__UncontainedExecutionAcknowledged`, `__UncontainedExecutionReason` |
| **Estado atual** | `Disabled`, com motivo declarado por escrito |
| **Para voltar atrás** | apagar as três linhas do `poseidon.env` |
| **Consequência de religar** | **as contas Claude Code param de autenticar** — a credencial vive no Keychain do macOS, que não existe dentro do contêiner. Foi medido: a mesma conta responde no host e responde "Invalid API key" no contêiner |

---

## Onde mexo para… (índice reverso rápido)

```
Bruna (quem ela é)      → ConversationChiefAgentExecutor.cs:672
Bruna (como ela fala)   → ChiefCommunicationPolicy.cs
Bruna (quando pergunta) → projects.<modo> (banco)
Modelo                  → NÃO EXISTE — criar em LaunchAsync/RunAsync
Effort                  → NÃO CHEGA — criar em ChiefBacklogLoopService.LaunchAsync:4229
Provider                → ExecutorCatalog.cs + <X>ExternalAgentExecutor.cs + Factory
Account                 → ~/.harness/agent-accounts.json
Fallback / cota         → AgentAccountScheduler.cs + AccountRecoveryBackgroundService.cs
Scheduler               → AgentAccountScheduler.cs (os 10 filtros)
Playbook                → CanonicalWorkflowTemplates.cs:150
Personas                → CanonicalAgentDefinitions.cs
Gates                   → CanonicalWorkflowTemplates.cs (texto) + PhaseGatePolicy.cs (mecânica)
Conselho                → AgentCouncilPolicy.cs
Contexto                → ContextBundleBuilder.cs + governance/manifest.yaml
Concorrência            → ~/.harness/poseidon.env + agent-accounts.json
Contenção               → ~/.harness/poseidon.env
```

---

## Depois de qualquer mudança em `src/`

O binário em execução **não** se atualiza sozinho, e isso já custou 310 linhas de log errado
em 03/08 (OPS-056). Sequência correta:

```bash
tools/backend/verify.sh                 # gates canônicos
tools/operation/publish-when-idle.sh    # pausa a esteira, publica, retoma
tools/operation/watchdog.sh             # confirma que o processo é mais novo que o último commit em src/
```
