# 03 — Fontes da verdade e precedência de configuração

> Onde cada comportamento é decidido, quem copia de quem, e quem lê em runtime.

---

## 1. A precedência real

O .NET compõe a configuração nesta ordem (a última vence):

```
appsettings.json
  → appsettings.<Ambiente>.json
    → variáveis de ambiente (Harness__Secao__Chave)
      → argumentos de linha de comando
```

Na prática, nesta instalação:

| Camada | Arquivo | O que define | Vence sobre |
|---|---|---|---|
| 1 | `src/Harness.Host/appsettings.json` | `Harness:IsolatedExecution` (contenção) | — |
| 2 | `~/.harness/poseidon.env` (exportado por `./poseidon start`) | `AgentRuns` (enabled, roots, timeouts, auto-dispatch, concorrência), `IsolatedExecution`, token do Telegram | appsettings |
| 3 | `~/.harness/agent-accounts.json` | **frota**: alias, provider, executor, papéis, path scopes, concorrência, prioridade, enabled | mescla **sobre** `CanonicalDefinitions` em código |
| 4 | Banco (`agent_definitions`, `providers`, `provider_models`, `projects`) | catálogo de personas, modo de autonomia do projeto | — |
| 5 | Código C# | playbook, política do Conselho, papéis, path scopes, persona da Bruna, catálogo de executores | **não é sobrescritível sem recompilar** |

**Um arquivo de contas inválido NÃO degrada em silêncio:** `AgentAccountConfigurationLoader`
lança `account.configuration_invalid`. Isso é correto e vale registrar.

---

## 2. Mapa por comportamento

| Comportamento | SOURCE OF TRUTH | Cópias derivadas | Consumidor em runtime | Precedência |
|---|---|---|---|---|
| **Quem é a Bruna (persona)** | `ConversationChiefAgentExecutor.ChiefPersona` (const C#) | `docs/agents/bruna.md` (**não consumido**) | `ConversationChiefAgentExecutor.BuildPrompt` | código > tudo |
| **Governança do turno da Bruna** | `governance/core.md` | `AGENTS.md`, `CLAUDE.md` (gerados, com checksum) | `LoadGovernanceCore()` — **caminho errado nesta instalação** (`F-04`) | fallback embutido |
| **Governança do trabalhador** | `governance/core.md` | `AGENTS.md` (Codex), `CLAUDE.md` (Claude Code) | **a CLI externa**, ao abrir a worktree | a CLI decide |
| **As 9 fases do playbook** | `CanonicalWorkflowTemplates.PlaybookStandard()` (C#) | `workflow_*_definitions` no banco (semeadas) | `WorkflowPhaseDriver`, `PhaseGatePolicy` | código semeia o banco |
| **Gates de cada fase** | idem (strings de critério dentro do template) | `workflow_gate_definitions` | `PhaseGatePolicy` | código |
| **Convocação do Conselho** | `AgentCouncilPolicy` (C#) | — | `WorkflowPhaseDriver` | código |
| **Papéis lógicos** | `AgentRoles` (C#, conjunto fechado de 4) | — | scheduler, path policy | código |
| **Escopo de path por papel** | `AgentRoles.PathScopesFor` + `AgentPathScopePolicy` | `allowedPathScopes` no `agent-accounts.json` (opcional; vazio = herda do papel) | `AgentRunOrchestrator` | **arquivo vence quando não vazio** |
| **Frota de contas** | `~/.harness/agent-accounts.json` | `AgentAccountConfigurationLoader.CanonicalDefinitions` (base) | `AgentAccountRegistry` | arquivo vence por alias |
| **Executores suportados** | `ExecutorCatalog` + `ExternalAgentExecutorFactory` (C#) | — | scheduler (filtros 2, 3, 4) | código |
| **Disponibilidade de conta** | `~/.harness/account-availability.json` (ledger persistido) | painel | `AccountAvailabilityLedger`, `AccountRecoveryBackgroundService` | ledger |
| **Catálogo de personas** | `CanonicalAgentDefinitions` (C#) | `agent_definitions` (semeadas) + endpoints CRUD | `ChiefTeamManager`, chief loop | código semeia; CRUD pode divergir |
| **Modelo por papel** | *(inexistente na prática)* | `agent_definitions.default_model_id` (vazio), `provider_models`, `agents.model_id` | **nada** no caminho autônomo (`F-05`) | — |
| **Effort por papel** | `agent_definitions.default_effort` | `agents.effort` | **nada** no caminho autônomo (`F-05`) | — |
| **Modo de autonomia** | `projects` (banco) | — | `PhaseGatePolicy` | banco |
| **Contenção/sandbox** | `Harness__IsolatedExecution__*` (env) | `appsettings.json` | `AgentRunOrchestrator` | env vence |
| **Auto-dispatch** | `Harness__AgentRuns__AutoDispatch*` (env) | `AgentRunSettings` (defaults C#) | `ChiefBacklogLoopService` | env vence |
| **Segredos** | **Keychain do macOS** | `credentialRef: keychain://...` | `AccountProfileProvisioner` | nunca em disco |

---

## 3. Arquivos de comportamento — existe / é carregado / por quem

| Arquivo | Existe | Tamanho | Carregado? | Por quem | Quando | Prioridade | Teste que prova |
|---|---|---|---|---|---|---|---|
| `governance/core.md` | ✅ | 104 linhas | **Sim, mas não pela Bruna** | worker CLI (via `CLAUDE.md`/`AGENTS.md` na worktree) | ao abrir a worktree | máxima | linter de manifesto |
| `governance/manifest.yaml` | ✅ | ~2100 linhas | ✅ | `GovernanceManifestService`, `GovernanceDocumentLinter` | verify + endpoint | — | sim |
| `AGENTS.md` | ✅ | 18 linhas | ✅ | **Codex CLI** | ao abrir o repositório | alta | checksum no header |
| `CLAUDE.md` | ✅ | 18 linhas | ✅ | **Claude Code CLI** | ao abrir o repositório | alta | checksum no header |
| `docs/agents/bruna.md` | ✅ | 136 linhas | ❌ | **ninguém** | — | — | **não** |
| `BRUNA.md` (raiz) | ❌ | — | — | — | — | — | — |
| `SKILLS.md` | ❌ | — | — | tabela `skills` no banco tem 5 linhas | — | — | — |
| `SPECS.md` | ❌ | — | — | — | — | — | — |
| `docs/INDEX.md` | ✅ | — | por convenção (o agente lê) | CLI | — | — | linter |

> **`AGENTS.md` e `CLAUDE.md` são arquivos GERADOS**, com `source=governance/core.md+governance/rules/*+governance/manifest.yaml`
> e `checksum=sha256:...` no cabeçalho. Editá-los à mão é errado; a fonte é `governance/`.
> Esse desenho está correto e é uma das partes mais maduras da governança.

> **`docs/agents/bruna.md` está marcado `DECORATIVO / NÃO CONSUMIDO`.** É o achado `F-04-B`.
> A ironia é que ele é o documento mais rico sobre como a Bruna deveria decidir.

---

## 4. Riscos de governança de configuração

| Risco | Descrição |
|---|---|
| **Configuração em código** | Playbook, gates, Conselho, papéis e persona da Bruna exigem **recompilar e republicar o binário**. Não há caminho de operador. Isso é defensável para invariantes de segurança; é caro para playbook e persona |
| **Duas verdades para persona** | `CanonicalAgentDefinitions` (C#) semeia `agent_definitions`, mas há endpoints CRUD. Uma persona editada pela API diverge do código na próxima migração |
| **Modelo/effort fantasma** | Painel e banco exibem `opus`/`medium`; a execução usa o default da CLI (`F-05`) |
| **Binário defasado** | Já custou 310 linhas de log errado em 03/08 (OPS-056). Existe agora `tools/operation/watchdog.sh` que data o processo e compara com o último commit em `src/` — mitigação **de script**, fora do produto |
