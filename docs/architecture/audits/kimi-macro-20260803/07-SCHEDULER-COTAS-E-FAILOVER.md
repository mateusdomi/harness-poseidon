# 07 — Scheduler, cotas e failover

> Este documento responde a pergunta estratégica da auditoria:
> **se a conta da Chief esgota, o Poseidon continua?**
>
> **Resposta: NÃO. A Chief é um single point of failure por configuração, e o produto não tem
> caminho de failover para o cargo mais importante da fábrica.**

---

## 1. O algoritmo real do scheduler

Arquivo: `src/Modules/Harness.Modules.Agents/Application/Accounts/AgentAccountScheduler.cs`
(269 linhas, puro, sem dependência de I/O — é testável e é testado).

### Entrada

```csharp
AccountSchedulingRequest {
    Role,                 // papel lógico exigido pelo card
    RequiredCapability,   // "chat" | "code" | "review"
    RequiredPathScopes,   // claims de arquivo do card
    PreferMostCapable,    // assimetria de modelo
    ForCritic, ActorAlias,// independência actor↔critic
    RiskTier, Now, Quotas
}
```

### Os 9 filtros, na ordem exata (fail-closed, primeiro que recusa vence)

| # | Filtro | Reason code de recusa |
|---|---|---|
| 1 | A conta tem o **papel**? | `account.role_not_allowed` |
| 2 | O executor tem **adapter implementado**? | `account.adapter_not_implemented` |
| 3 | O executor existe no **catálogo**? | `account.executor_unknown` |
| 4 | O executor declara a **capability** pedida? | `account.capability_unsupported` |
| 5 | Estado administrativo | `account.disabled` / `account.executor_unavailable` / `account.authentication_required` |
| 6 | **Cota** esgotada (snapshot ou estado persistido + cooldown) | `account.quota_limited` |
| 7 | **Cooldown** transitório | `account.cooling_down` |
| 8 | **Concorrência** (`ActiveAttempts >= ConcurrencyLimit`) | `account.concurrency_exhausted` |
| 9 | **Escopo de path** (por continência de raiz, não igualdade) | `account.path_scope_not_allowed` |
| 10 | **Independência** actor↔critic | `account.actor_cannot_be_critic` |

### Saída e desempate

```
elegíveis
  ORDER BY  (PreferMostCapable ? -Priority : +Priority)
  THEN BY   PreferenceRank   // eligible=0 < degraded=1 < near_limit=2 < ambos=3
  THEN BY   Alias (ordinal)  // desempate determinístico
```

`AccountSelectionDecision` carrega **todos** os candidatos com o motivo de cada um — elegível ou
não. Isso é exatamente a "explicação de inelegibilidade" pedida no §22 da ordem de auditoria, e
**ela existe**. Ela chega ao log (`LogCardDeferred` imprime `alias:reason`), mas
**não chega ao card nem à interface** — ver achado `F-07`.

### O que é bom aqui

- Conjuntos fechados de estado e de motivo; disponibilidade nunca inferida de ausência de dado.
- Prioridade nunca promove um inelegível.
- `near_limit` **de-prefere** sem bloquear — descartar capacidade real seria pior.
- Medição vencida (`IsStale`) não de-prefere ninguém.
- **Starvation:** tratada em duas camadas — desempate determinístico por alias e, no
  `ChiefBacklogPolicy`, ordenação por antiguidade. Há registro de uma correção específica de
  "inanição por idade" em 03/08.

---

## 2. A frota real desta instalação

Fonte: `~/.harness/agent-accounts.json` (mesclado sobre `AgentAccountConfigurationLoader.CanonicalDefinitions`).

| Alias | Provider | Executor | Papéis | Path scopes | Conc. | Prior. | Habilitada | Adapter? | Capabilities |
|---|---|---|---|---|---|---|---|---|---|
| `chief-claude-primary` | anthropic | `claude-code` | **chief-orchestrator** | (herda do papel: nenhum) | 1 | 100 | sim | ✅ | chat, code, review |
| `worker-claude-secondary` | anthropic | `claude-code` | backend-specialist, frontend-specialist | src/**, tests/**, docs/**, infra/**, frontend/** | 2 | 100 | sim | ✅ | chat, code, review |
| `worker-codex-frontend` | openai | `codex` | frontend-specialist | frontend/**, docs/frontend/** | 3 | 100 | sim | ✅ | chat, code, review |
| `worker-codex-critic` | openai | `codex` | **critic** | docs/conselho/** | 3 | 80 | sim | ✅ | chat, code, review |
| `worker-antigravity-review` | antigravity | `antigravity` | **critic** | docs/conselho/** | 3 | 90 | sim | ✅ | chat, code, review |
| `worker-kimi-ui` | moonshot | `kimi-code` | frontend-specialist | frontend/**, docs/frontend/** | 2 | 70 | sim | ❌ **NÃO** | chat, code (**sem review**) |
| `worker-glm-general` | zhipu | `glm` | backend-specialist | (herda) | 1 | 60 | **não** | ✅ | chat, code, review |

### Fatos que contradizem a percepção do proprietário

> "Kimi: COM COTA"

**Kimi nunca executou e não pode executar nada.** Dois bloqueios independentes:

1. `ExternalAgentExecutorFactory.IsImplemented()` aceita apenas
   `claude-code`, `glm`, `codex`, `antigravity`. **`kimi-code` não está na lista** — o comentário
   da classe diz literalmente *"Kimi Code entra depois"*. Recusa: `account.adapter_not_implemented`.
2. Mesmo se o adapter existisse, `ExecutorCatalog` declara para Kimi apenas
   `["chat","code"]` — **sem `review`** — e o papel dela é `frontend-specialist` apenas.

> "Antigravity: papel atual predominante critic"

Correto, e é a **única conta critic operacional** hoje (Codex sem cota). Ela é executor de
primeira classe, com a maior prioridade entre os críticos (90 > 80).

> "GLM: assinatura cancelada"

Confirmado em código: GLM saiu do catálogo canônico em 03/08 com comentário explicando que o
adaptador fica (é o binário do Claude Code apontado a outro endpoint). O arquivo local ainda o
declara `enabled:false` e `~/.harness/account-availability.json` o mostra `QuotaLimited` até
`2026-08-06T10:11Z`.

---

## 3. Failover da Chief — a análise crítica

### Como a conta da Chief é escolhida

```csharp
// ConversationChiefAgentExecutor.cs:207
private AgentAccountContract? ResolveChiefAccount() =>
    _accounts.List()
        .Where(a => a.State != Disabled &&
                    a.AllowedRoles.Contains("chief-orchestrator"))
        .OrderByDescending(a => a.Priority)
        .ThenBy(a => a.Alias)
        .FirstOrDefault();
```

**Leia com atenção o que este código NÃO faz:**

- não consulta cota;
- não consulta cooldown;
- não consulta concorrência;
- não consulta `AccountAvailabilityLedger`;
- **não usa o `AgentAccountScheduler`.**

Ou seja: o caminho da Chief **tem um seletor próprio, mais pobre que o do resto da fábrica**.
Se a conta estiver sem cota, ele a escolhe assim mesmo; a falha aparece depois, na execução da CLI,
e o turno falha. Não há reeleição.

### E se houvesse outra conta com o papel?

Ela seria escolhida por prioridade — o código **suporta** múltiplas contas de Chief.
Mas na configuração real **só existe uma**: `chief-claude-primary`.

### Resposta à pergunta do §10

> Com Claude principal sem cota, Claude B com cota, Kimi com cota e Antigravity com cota,
> **quais são elegíveis para executar uma tarefa de Chief?**

| Conta | Elegível como Chief? | Por quê |
|---|---|---|
| `chief-claude-primary` | Sim (é a única) | Único alias com `allowedRoles:["chief-orchestrator"]` |
| `worker-claude-secondary` | **NÃO** | `account.role_not_allowed` — papéis são backend/frontend specialist |
| `worker-antigravity-review` | **NÃO** | `account.role_not_allowed` — papel `critic` |
| `worker-kimi-ui` | **NÃO** | `account.role_not_allowed` + `account.adapter_not_implemented` |
| `worker-codex-frontend` | **NÃO** | `account.role_not_allowed` |

**Veredito: `Chief failover` = NÃO SUPORTADO na configuração atual; PARCIALMENTE SUPORTADO na
arquitetura** (o mecanismo de múltiplas contas por papel existe; a Chief simplesmente não o usa
e não há segunda conta declarada).

### O caminho de correção (não aplicado nesta sessão — é auditoria)

1. **Configuração pura, 2 minutos:** adicionar `"chief-orchestrator"` a `allowedRoles` de
   `worker-claude-secondary` em `~/.harness/agent-accounts.json` e reiniciar o Host.
   Isso já dá failover Claude→Claude. **Custo: zero de código.**
2. **Código, pequeno:** trocar `ResolveChiefAccount()` por uma chamada ao
   `AgentAccountScheduler` com `Role="chief-orchestrator"`, `RequiredCapability="chat"`.
   Passa a respeitar cota, cooldown e concorrência de graça.
3. **Para Chief em Kimi:** exige o adapter `KimiCodeExternalAgentExecutor` (não existe) e
   validar o contrato de saída JSON com aquela CLI.

---

## 4. Trocar Bruna de Claude para Kimi — resposta direta

**PARCIAL — hoje, NÃO; a arquitetura permite, a implementação não.**

O que **já está desacoplado** (e é mérito real do desenho):

- persona, governança, política de comunicação e contrato de saída são **texto e schema**,
  independentes de provedor;
- o escopo de path pertence ao **papel**, nunca ao provider (`AgentRoles`, comentário CA-1);
- o histórico do produto vive em `conversations`/`conversation_messages`, não no fornecedor;
- `ExecutorCatalog` e `ExternalAgentExecutorFactory` são a única fronteira que conhece provedor;
- `AgentPathScopeKind.Kimi = FrontendSpecialist` — um alias de compatibilidade, com comentário
  dizendo *"o papel nunca foi propriedade de um provider"*.

O que **impede**, concretamente:

| Bloqueio | Onde | Esforço para remover |
|---|---|---|
| Adapter `kimi-code` não existe | `ExternalAgentExecutorFactory.Create()` | **Médio** — 1 classe, ~200 linhas, espelhando `CodexExternalAgentExecutor`; precisa de parsing de saída e mapeamento de falha para `ExternalFailureKind` |
| Kimi não declara capability `review` | `ExecutorCatalog.All` | Trivial (1 linha), **se** for verdade que a CLI faz review |
| Kimi não tem o papel `chief-orchestrator` | `~/.harness/agent-accounts.json` | Trivial (config) |
| Contrato de saída JSON da Bruna | `ChiefTurnOutputContract` | **Risco real** — a Bruna exige JSON estrito; a CLI do Kimi pode não sustentar isso com a mesma taxa de acerto. Há reparo de 1 tentativa e degradação para `unmatched`, então falha degrada, não quebra |
| Continuidade de sessão | `--resume` da CLI | O histórico do modelo se perde na troca; o do produto não |

**Como seria, se o adapter existisse:** trocar `allowedRoles` no `agent-accounts.json`,
reiniciar o Host. Não há mudança de código de domínio. **Isto é um bom sinal de arquitetura.**

**Classificação:** `ARCHITECTURAL RISK` (alto valor estratégico), não `DEFECT`.

---

## 5. Quota balancing — como o sistema sabe

| Pergunta | Resposta | Onde |
|---|---|---|
| Como sabe que uma conta acabou? | O **adaptador** declara `ExternalFailureKind.QuotaExhausted` lendo a frase do fornecedor. Nunca substring do nosso próprio código. | `ClaudeCodeExternalAgentExecutor`, `CodexExternalAgentExecutor` |
| Como sabe quando volta? | Lê o **instante declarado pelo provedor** no texto (ex.: "session limit... 11:30"). Sem isso, cooldown por política. | idem + `AccountAvailabilityLedger` |
| Como evita reelegê-la? | Filtro 6 do scheduler + `account-availability.json` persistido | `AgentAccountScheduler` |
| Como escolhe outra? | Próximo elegível por prioridade | idem |
| Starvation? | Desempate por alias + ordenação por idade no `ChiefBacklogPolicy` | |
| Se TODAS acabarem? | `scheduler.no_eligible_account` → card adiado com motivo → `AnnounceUndispatchableCardsAsync` avisa o dono quando o adiamento é **estrutural** | `ChiefBacklogLoopService` |
| Recuperação | `AccountRecoveryBackgroundService`, a cada **1 minuto** | |

### Isto vale igualmente para Chief / Actor / Critic / Council?

**NÃO — e esta é a resposta mais importante desta seção.**

| Caminho | Usa o `AgentAccountScheduler`? | Consulta cota? |
|---|---|---|
| **Actor** (card comum) | **SIM** | SIM |
| **Council** (parecer) | **SIM** (é um card comum de papel `critic`) | SIM |
| **Critic** (revisão de tentativa) | **NÃO** — usa `ChiefBacklogLoopService.SelectCriticAliases()`, um seletor próprio de 20 linhas | SIM (cooldown + capacity signals), mas com filtros diferentes |
| **Chief** (turno de conversa) | **NÃO** — usa `ResolveChiefAccount()` | **NÃO** |

Existem **três seletores de conta** no produto. Só um deles é o scheduler auditado e testado.
Achado `F-01` (Chief) e `F-02` (Critic).

---

## 6. Modelo e effort — o achado de governança

**Nenhum run despachado autonomamente carrega `--model` nem `--effort`.**

Cadeia verificada:

```
ChiefBacklogLoopService.cs:951   providerRouting.RouteAndAuditAsync(..., preferredModel: null, ...)
ModelRouter.cs:73                SelectedModel: request.PreferredModel      // ou seja: null
ChiefBacklogLoopService.cs:4229  Model = model                             // null
ChiefBacklogLoopService.cs:4230+ (Effort nunca é atribuído)                // null
ClaudeCodeExternalAgentExecutor.cs:153  if (request.Model is {Length:>0})  // falso → sem --model
ClaudeCodeExternalAgentExecutor.cs:158  if (request.Effort is {Length:>0}) // falso → sem --effort
```

Consequência: **toda a frota roda no modelo e no esforço padrão de cada CLI.**

E ao mesmo tempo:

- `agent_definitions.default_effort` está preenchido (`high` para 22 personas, `medium` para 9)
  — **e não é usado no despacho autônomo**;
- `agent_definitions.default_model_id` está **vazio** para todas as personas;
- a tabela `provider_models` tem 2 linhas (`claude-sonnet-4-5`, `opus`) com mapeamento de effort
  declarado — **e nada as consulta neste caminho**;
- a tabela `routing_policies` está **vazia**;
- `agents` (instâncias) tem `effort='medium'` e `model_id='opus'` gravados — **rótulo de catálogo,
  não flag de execução**.

O único caminho que respeita modelo/effort é o **endpoint HTTP manual**
(`AgentRunEndpoints.cs:394-395`).

**Achado `F-05` — HIGH, OBSERVABILITY GAP + TECH DEBT.** O sistema **não consegue responder de
forma centralizada** qual modelo e qual esforço cada papel usa, porque na prática a resposta é
"o default da CLI", e o painel mostra outro valor. A "assimetria de modelo" descrita como
Fase 1B no `AccountSchedulingRequest.PreferMostCapable` opera sobre **prioridade de conta**,
não sobre modelo.

---

## 7. Achados desta seção

| ID | Sev. | Tipo | Título | Bloqueia piloto? | Bloqueia autonomia? |
|---|---|---|---|---|---|
| `F-01` | **CRITICAL** | ARCHITECTURAL RISK | Chief é single point of failure: um único alias com o papel, e um seletor próprio que ignora cota e não reelege | Não | **SIM** |
| `F-02` | **HIGH** | ARCHITECTURAL RISK | Três seletores de conta distintos (Chief, Critic, Scheduler) com regras diferentes | Não | **SIM** |
| `F-05` | **HIGH** | TECH DEBT | Modelo e effort nunca chegam à CLI no caminho autônomo; catálogo e painel mostram valores que não são aplicados | Não | Não |
| `F-07` | **MEDIUM** | OBSERVABILITY GAP | A explicação de inelegibilidade existe e é boa, mas morre no log — não chega ao card nem à tela | Não | Não |
| `F-09` | **MEDIUM** | TECH DEBT | `worker-kimi-ui` está na frota, habilitada, e nunca poderá executar (`kimi-code` sem adapter). Uma conta que não executa deveria nascer `Unavailable` visível, não elegível-e-recusada | Não | Não |
| `F-10` | **LOW** | TECH DEBT | Antigravity não declara `ExternalFailureKind` — suas falhas voltam à heurística de substring legada; e não reporta uso (`usage_unknown`, `output_tokens=0` em todas as 6 invocações do Conselho) | Não | Não |
