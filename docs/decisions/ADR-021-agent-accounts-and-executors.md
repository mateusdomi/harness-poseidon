# ADR-021 — Contas de agente por alias, executores reais e papel provider-agnostic

- Status: aceito
- Data: 2026-07-21

## Contexto

Para o Chief orquestrar workers reais é preciso representar contas de múltiplos provedores
(Claude Code, Codex, Antigravity, Kimi Code, GLM) sem vazar identidade nem segredo, e sem
que a governança confunda **papel lógico** com **marca do executor**. A RC3 amarrava o
escopo de frontend ao provider (`AgentPathScopeKind.Kimi`,
`KimiAgentDefinitionKeys`), impedindo que o Codex exercesse o mesmo papel.

## Decisão

### 1. Papel lógico separado do executor (CA-1)

O escopo de paths pertence ao **papel**, nunca ao provider. `AgentPathScopeKind` passa a
expor `FrontendSpecialist`, com `Kimi` mantido como alias do mesmo valor por
compatibilidade. A lista de definições do papel
(`FrontendSpecialistAgentDefinitionKeys`) aceita `frontend-specialist`, `frontend-kimi`,
`kimi`, `kimi-code`, `frontend-codex` e `codex-frontend`; a chave de configuração histórica
`KimiAgentDefinitionKeys` continua funcionando e, quando informada, prevalece.

Trocar o executor **não amplia escopo**: backend continua proibido em `frontend/**`, CLI
direta sem claim continua bloqueada e claim expirado/conflitante continua bloqueando.

### 2. Identidade por alias, nunca por e-mail (CA-2)

Contas são identificadas por **alias** técnico estável (minúsculas, dígitos, hífen). Um
valor contendo `@` é rejeitado explicitamente: e-mail é identidade real de pessoa e nunca
entra no registro, no banco, em log ou em evidência. A associação alias → conta real vive
somente em configuração local protegida.

Aliases canônicos sugeridos (configuráveis): `chief-claude-primary`,
`worker-claude-secondary`, `worker-codex-frontend`, `worker-codex-critic`,
`worker-kimi-ui`, `worker-glm-general`, `worker-antigravity-review`.

### 3. Credencial sempre por referência opaca

A conta guarda um `credentialRef` com esquema allowlisted (`keychain://`, `secret://`,
`env://`). Um valor sem esquema — isto é, o próprio segredo — é **recusado**. O segredo
pertence ao Keychain/secret store e nunca trafega pelo contrato.

### 4. Catálogo de executores reflete CLIs reais

Cada `ExecutorProfile` declara comando, flags de execução não interativa e a variável REAL
de isolamento, observados por probe nesta máquina — nunca nomes inventados:

| Executor | Comando | Não interativo | Config home |
|---|---|---|---|
| `claude-code` | `claude` | `-p --output-format stream-json` | `CLAUDE_CONFIG_DIR` |
| `codex` | `codex` | `exec --skip-git-repo-check` | `CODEX_HOME` |
| `antigravity` | `agy` | `--print` (prompt posicional) | (sem variável; auth em `~/.gemini/antigravity-cli`, isolar por HOME) |
| `kimi-code` | `kimi` | `-p` | (sem variável documentada) |
| `glm` | `claude` | `-p --output-format stream-json` | `CLAUDE_CONFIG_DIR` |

**GLM é o Claude Code apontado a um endpoint compatível** por variáveis de ambiente
(`ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, mapeamento de modelos). Como usa o **mesmo
binário** das contas Claude, exige `CLAUDE_CONFIG_DIR` próprio — sem isso, duas contas
sobrescreveriam a autenticação uma da outra. Isso é verificado por teste.

**Antigravity não é experimental**: a CLI `agy` está instalada e é usada em produção pelo
operador para code review. É modelada como executor de primeira classe, com vocação de
critic. O adapter real (`AntigravityExternalAgentExecutor`), o perfil isolado e o critic
read-only Default-FAIL foram entregues em N3 (ver `evidence/N3-ANTIGRAVITY-CRITIC.md`). Dois
achados de probe corrigem suposições anteriores: (a) o `agy --print` só aceita o prompt como
argumento posicional e imprime texto puro (sem JSON/stream); (b) ele sai com código 0 mesmo
sem autenticar — a falha é classificada pela SAÍDA, nunca pelo exit code. O login é OAuth
interativo (`agy`), portanto o perfil isolado exige ação humana única e o smoke live fica
`BLOCKED_EXTERNAL_OAUTH` até lá.

### 5. Concessão de conta com fencing

Reservar uma conta gera `AccountLease` com fencing crescente. Um fencing antigo não libera
a concessão vigente; dupla reserva, limite de concorrência e conta em cota são recusados
com código tipado.

## Consequências

- O Codex passa a exercer `frontend-specialist` sem bypass de governança.
- Nenhum e-mail, token ou senha entra no Git, no banco ou em evidência.
- O scheduler (CA-6) tem estados fechados e decisão explicável por código.
- Executor ausente é `Unavailable` por probe, nunca "suportado por suposição".
