# 06 — Providers, contas, modelos e effort

---

## 1. Os 5 executores do catálogo

`src/Modules/Harness.Modules.Agents/Application/Accounts/ExecutorCatalog.cs` — perfis observados
por probe em CLI real, não inventados.

| Executor | Comando | Flags não-interativas | Isolamento de credencial | Capabilities | Streaming | Resume | Effort | Adapter |
|---|---|---|---|---|---|---|---|---|
| `claude-code` | `claude` | `-p --output-format stream-json --verbose` | `CLAUDE_CONFIG_DIR` | chat, code, review | ✅ | ✅ | `low/medium/high/xhigh/max` | ✅ |
| `codex` | `codex` | `exec --skip-git-repo-check` | `CODEX_HOME` | chat, code, review | ✅ | ✅ | ❌ | ✅ |
| `antigravity` | `agy` | `--print` (texto puro) | **nenhuma** — isolamento por `HOME` | chat, code, review | ❌ | ✅ | `low/medium/high` | ✅ |
| `kimi-code` | `kimi` | `-p` | **nenhuma** | chat, **code** (sem review) | ✅ | ✅ | ❌ | ❌ **ausente** |
| `glm` | `claude` (mesmo binário, outro endpoint) | idem claude-code | `CLAUDE_CONFIG_DIR` | chat, code, review | ✅ | ✅ | idem claude | ✅ |

### Detalhes com valor operacional (todos registrados no próprio código)

- **`USER` é obrigatório no ambiente do Claude Code** — sem ele a CLI não alcança o item do
  Keychain no macOS e responde "Not logged in" mesmo com a conta vinculada. Descoberto por
  bisecção de ambiente no Piloto 1A.
- **Antigravity não tem config home dedicado.** A autenticação vive em
  `$HOME/.gemini/antigravity-cli`, então o isolamento por conta é feito apontando `HOME` para o
  config home do alias. Consequência declarada: o login é interativo e o perfil isolado exige
  **OAuth humano** — sem ele o smoke ao vivo é `BLOCKED_EXTERNAL_OAUTH`.
- **GLM é o binário do Claude Code apontado a outro endpoint** por variáveis de ambiente. Por isso
  exige config home próprio, senão sobrescreve a sessão das contas Claude.

---

## 2. Auth, quota e taxonomia por provider

| Provider | Auth | Detecção de cota | Detecção de reset | Cooldown | Taxonomia tipada | Persistência de sessão | Sandbox |
|---|---|---|---|---|---|---|---|
| Claude Code | OAuth/Keychain macOS | ✅ lê a frase do provedor ("You've hit your session limit") | ✅ **lê o instante declarado no texto** | ✅ | ✅ `ExternalFailureKind` | `--resume <sessionId>` | worktree (contêiner desligado) |
| Codex | OAuth, `CODEX_HOME` | ✅ | parcial | ✅ | ✅ `ExternalFailureKind` | ✅ | idem |
| Antigravity | OAuth interativo, isolamento por `HOME` | ⚠️ genérica | ❌ | ✅ (por política) | ❌ **cai em `Unknown`** | `--conversation <id>` | idem |
| Kimi | — | — | — | — | — | — | — |
| GLM | token via env (Keychain) | ✅ | ✅ | ✅ | herda do Claude Code | ✅ | idem |

**Achado `F-10`** já registrado: Antigravity não declara `ExternalFailureKind` e não reporta uso.
Todas as 6 invocações do Conselho ficaram `completed|usage_unknown` com `output_tokens=0` — logo,
**custo e consumo da única conta critic viva são invisíveis** na contabilidade
(`METRICS.json` mede 963 invocações e US$ 302,96, sem nada da Antigravity).

---

## 3. Modelo e effort — a auditoria pedida no §12

### O que o sistema declara

| Onde | Conteúdo |
|---|---|
| `agent_definitions.default_effort` | `high` (22 personas) / `medium` (9) |
| `agent_definitions.default_model_id` | **vazio em todas as 35** |
| `provider_models` (banco) | 2 linhas: `claude-sonnet-4-5` e `opus`, ambas com `effort_mappings_json` completo e contexto 200k |
| `providers` (banco) | 3: OpenAI, Anthropic (habilitados), Ollama (desabilitado) |
| `provider_accounts` (banco) | **1 linha**: Chefe — Claude Code, plano `pro`, auth `oauth` |
| `routing_policies` (banco) | **vazia** |
| `agents` (instâncias) | `model_id='opus'`, `effort='medium'` |

### O que o sistema efetivamente executa

**Nada disso.** A cadeia (verificada linha a linha) é:

```
ChiefBacklogLoopService:951   RouteAndAuditAsync(..., preferredModel: null, ...)
ModelRouter:73                SelectedModel = request.PreferredModel   // null
ChiefBacklogLoopService:4229  Model = model                           // null
                              (Effort nunca é atribuído)              // null
ClaudeCodeExecutor:153-161    if (Model is {Length:>0}) ...           // falso
                              if (Effort is {Length:>0}) ...          // falso
```

**Resultado: a CLI é chamada sem `--model` e sem `--effort`.** Todo run autônomo usa o
**default do binário instalado**.

### Resposta pedida por executor

| Executor | Valor atual em execução | Default | Onde é configurado | Precedência | Como alterar | Exige restart |
|---|---|---|---|---|---|---|
| claude-code | **default da CLI** | default da CLI | *(nada no caminho autônomo)* | — | passar `preferredModel` em `RouteAndAuditAsync` e `Effort` em `StartAgentRunCommand` | **sim, recompilar** |
| codex | default da CLI | default da CLI | — | — | idem | sim |
| antigravity | default da CLI | default da CLI | — | — | idem | sim |
| Endpoint HTTP manual | o que o cliente mandar | — | `AgentRunEndpoints.cs:394-395` | maior | corpo da requisição | não |

> **Registro formal, como a ordem de auditoria pediu:** o sistema **NÃO consegue responder de
> forma centralizada** qual modelo e qual esforço cada papel usa. Pior: o painel e o banco
> respondem `opus`/`medium`, e a execução real é outra coisa. Isto é **problema de governança de
> configuração**, achado `F-05`, severidade **HIGH**.

---

## 4. Segurança, credenciais e contenção (visão macro, §37)

| Eixo | Estado |
|---|---|
| **Segredos** | Nunca em disco do produto. `credentialRef: keychain://poseidon/<alias>` — referência opaca. `AgentAccountRegistry.ValidateCredentialReference` valida o formato. Token do Telegram resolvido do Keychain na hora do `start` |
| **Isolamento de credencial por conta** | `AccountProfileProvisioner` cria config home por alias em `~/.harness/accounts/<alias>` e aponta a variável do executor |
| **Sandbox / contêiner** | `Harness__IsolatedExecution__Mode=Disabled` **nesta instalação**, por decisão registrada do proprietário em 03/08 — o contêiner não continha risco, **cegava a frota** (Keychain do macOS não existe dentro dele; a mesma conta responde no host e responde "Invalid API key" no contêiner). A decisão está declarada em `appsettings.json` **e** no env, com motivo por escrito e instrução de reversão |
| **Worktree** | Uma por tentativa, sempre. **Continua valendo** |
| **Claims de path por card** | Continuam valendo (`attempt_scope_claims`) |
| **Allowlist de ferramentas** | Continua valendo (`SecurityPolicyEnforcementPoint`, `persona.ToolIds`, fail-closed em `null`) |
| **Actor vs critic** | Crítico roda com `--tools` **sem** `Edit/Write/Bash` e `--permission-mode dontAsk`. Não depende de o modelo "resolver não escrever" |
| **Rede / proxy de egresso** | Existe no desenho (`infra/`), **inativo** com o contêiner desligado |
| **Atestação** | `sandbox_attestations` no banco; `UncontainedExecutionAcknowledged` gera log de aviso por run |

**Avaliação:** a postura de segurança é séria e a decisão de desligar a contenção foi tomada com
justificativa medida e caminho de volta. O que se perdeu foi **contenção de processo**; o que
permanece é isolamento de credencial, de diretório, de escopo de arquivo e de ferramenta.
Para piloto interno é aceitável. Para produto vendido a terceiro, é `ARCHITECTURAL RISK`
(`F-19`, MEDIUM) — e a razão raiz (credencial de assinatura no Keychain) é a mesma que o
documento de arquitetura já sinaliza como restrição contratual do modo servidor.

---

## 5. Multiprojeto e concorrência (§34, §35, §36)

| Pergunta | Resposta |
|---|---|
| Quantos projetos simultâneos? | 27 no banco. Não há limite declarado |
| Scheduler global ou por projeto? | **Global** — `AgentAccountRegistry` é singleton; o `ChiefBacklogLoopService` itera projetos e despacha contra a **mesma** frota |
| Fairness entre projetos | Existe teto por ciclo (`AutoDispatchMaxConcurrent=4`) e contagem por projeto (`dispatchCounts`); **não há cota justa por projeto** |
| Tenant isolation | Sim, `tenant_id` em todas as tabelas; chaves compostas `(tenant, project, id)` |
| Repository locks | `HasLiveScopeConflict` + `attempt_scope_claims` impedem dois runs no mesmo escopo de arquivos |
| Resource locks | Lease com fencing token (`agents.lease_fencing_token`) |
| Cota compartilhada | **Sim** — a frota é global; um projeto pode consumir a cota de outro |
| Prioridade entre projetos | Não existe |

### Concorrência — onde cada limite mora

| Limite | Onde se configura | Valor atual |
|---|---|---|
| Despacho por ciclo do chefe | `Harness__AgentRuns__AutoDispatchMaxConcurrent` | **4** |
| Intervalo do ciclo | `Harness__AgentRuns__AutoDispatchInterval` | 10 s |
| Por conta | `concurrencyLimit` no `agent-accounts.json` | 1 a 3 |
| Por provider | ❌ **não existe** | — |
| Por repositório | por escopo de arquivo (`HasLiveScopeConflict`), não por repo | — |
| Crítico | herda o `concurrencyLimit` da conta critic | 3 |
| **HEAVY (builds)** | ❌ **NÃO EXISTE** | — |

### §36 — existe algo que impeça 7 agentes → 7 builds `dotnet` simultâneos?

**NÃO.** Não há `ResourceCoordinator`, nem slot HEAVY, nem semáforo de build no produto.
O que existe é o teto de **despacho** (`AutoDispatchMaxConcurrent=4`), que limita quantas
tentativas correm — e uma tentativa pode disparar quantos builds quiser dentro da worktree.

Isso é conhecido operacionalmente (a memória do projeto registra "7 agentes = 35 GB e travamento")
e hoje é mitigado **por disciplina humana e scripts**, não pelo produto.

**Achado `F-20` — HIGH, ARCHITECTURAL RISK.** Não confundir teto de agentes com slot de recurso
pesado. Numa instalação de cliente, isso derruba a máquina.
