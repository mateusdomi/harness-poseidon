# N3 — Antigravity first-class: adapter, perfil isolado e critic Default-FAIL

Data: 2026-07-22. Branch: `develop`. Base: `e284014` (Piloto 1 fechado). Continuação de
ADR-021 (CA-4/CA-7) sobre os perfis de CA-3.

## Objetivo

Tratar o Antigravity (`agy`) como executor de PRIMEIRA CLASSE — não experimental — com
vocação de critic. Implementar o adapter real, o perfil isolado, o modo critic read-only
com Default-FAIL, testes e o comando exato de login isolado. Sem inventar flags: tudo
abaixo foi OBSERVADO por probe na CLI instalada.

## Probe real do `agy` (CLI 1.1.5, arm64)

`agy --help`, `agy help install`, execução real e execução sob HOME isolado. Achados:

| Aspecto | Observado |
|---|---|
| Versão | `1.1.5` (via `agy --version`, exit 0) |
| Não interativo | `--print` / `-p` / `--prompt` — roda um prompt único e imprime a resposta |
| Entrega do prompt | **Argumento posicional**: `agy --print "<prompt>"`. STDIN **não** é lido como prompt (probe: `GIRAFFE-ARG-4482` só voltou pela via posicional) |
| Saída estruturada | **Nenhuma** — texto puro; não há `--json`/stream (`SupportsStreaming=false`) |
| Timeout | `--print-timeout <dur>` (padrão `5m0s`); duração Go (`1800s`) |
| Retomada de sessão | `--conversation <id>` (por ID) e `--continue`/`-c` (a mais recente) |
| Modelo / esforço | `--model <m>`; `--effort low\|medium\|high` |
| Modo de escrita | `--mode plan` (não edita) / `--mode accept-edits`; `--dangerously-skip-permissions` |
| Sandbox | `--sandbox` (restrições de terminal) |
| Config home / auth | `$HOME/.gemini/antigravity-cli/` e `$HOME/.gemini/config/`. **Sem variável dedicada** de config home → isolamento por HOME |
| Login / OAuth | **Não há subcomando `login`/`auth`**. O erro instrui `Run 'agy' to log in` — login é OAuth interativo pelo binário `agy` |
| Exit codes | `agy` sai **0 mesmo sem autenticar** e mesmo quando uma ferramenta é auto-negada em headless. A falha vem pela SAÍDA, nunca pelo exit code |
| Cancel | Cooperativo por fechar stdin + kill da árvore de processos (via sessão genérica) |

Correção a ADR-021: o config home NÃO é `~/.antigravity`; é `~/.gemini/antigravity-cli`, e o
isolamento se dá pelo HOME (o provisionador aponta HOME para o config home do alias, CA-3).

## Adapter — `AntigravityExternalAgentExecutor`

`ProcessExternalAgentExecutor` já concentra probe, ambiente por allowlist do perfil isolado,
recusa de segredo em argv, criação e ciclo de vida da sessão (`stream`/`collect`/`cancel`/
`stop`/`cleanup`, `sessionId`/`processId`/`isRunning`, kill da árvore no dispose). O adapter
Antigravity acrescenta só o que é específico do `agy`:

- **`BuildArguments`**: `--print`, `--print-timeout <segundos>s`, `--conversation <id>` na
  retomada, `--model`, `--effort`; **critic** → `--mode plan --sandbox` (sem
  `--dangerously-skip-permissions`); **actor** → `--mode accept-edits --dangerously-skip-permissions`;
  `--add-dir` por diretório extra.
- **Entrega do prompt**: novo ponto de extensão `PromptDelivery` na base. O padrão continua
  STDIN (Claude/Codex, para não vazar o prompt na tabela de processos); o Antigravity
  declara `PositionalArgument` — limitação REAL do binário. O prompt posicional passa pelo
  MESMO guard estrutural de segredo (`executor.secret_in_prompt`).
- **Parser de texto**: acumula o stdout como resposta; detecta as sentinelas observadas
  (`authentication required`/`failed` → `executor.authentication_required`; `no output
  produced` → `executor.tool_permission_denied`). Como o `agy` imprime a falha de auth em
  STDERR e ainda sai 0, a sentinela é observada também no STDERR por `ObserveErrorLine`
  (que só toca `FailureCode`, lido após a junção dos fluxos — o canal de eventos permanece
  single-writer). Resposta vazia sem sentinela → `executor.no_output` (fail-closed).

Registrado em `ExternalAgentExecutorFactory` (`IsImplemented` + `Create`), portanto o
orquestrador o resolve como actor OU critic exatamente como Claude/Codex.

## Perfil isolado `worker-antigravity-review`

Provisionado por `AccountProfileProvisioner` sob `~/.harness/accounts/worker-antigravity-review/`
(fora do repositório, permissões só-dono). Como o `agy` não tem variável de config home, o
ambiente do subprocesso aponta **HOME** para o config home do alias. Prova de isolamento: ao
rodar o smoke, o `agy` criou seu estado em
`~/.harness/accounts/worker-antigravity-review/config/.gemini/antigravity-cli/` — **nunca** no
HOME do operador. A autenticação global do operador (sidecar/language server) não é copiada.

## Critic read-only e Default-FAIL

O critic Antigravity roda `--mode plan --sandbox` (não edita, terminal restrito) e **sem**
`--dangerously-skip-permissions` — logo qualquer ferramenta é auto-negada em headless: é
fail-closed por construção, sem depender de o modelo "decidir não escrever". Não recebe
worktree de escrita nem adquire claim de path (o orquestrador concede `Access=ReadOnly`).

O veredito é fechado por `CriticReviewContract` (CA-7, já existente): Default-FAIL para saída
ausente, JSON inválido, veredito ausente/fora do conjunto, e PASS contradito por P0/P1.

## Testes

- **`AntigravityExternalAgentExecutorTests` (7, unit)**: resolvível pela factory; critic em
  `--mode plan --sandbox` sem skip-permissions; actor em `accept-edits`+skip-permissions;
  `--print-timeout`/`--conversation`/`--model`/`--effort` reais; effort fora de
  `low|medium|high` recusado (`executor.effort_unsupported`); prompt fora do argv em
  `BuildArguments`; prompt com forma de segredo recusado (`executor.secret_in_prompt`).
- **`AntigravityAdapterProcessTests` (4, integração, `agy` FALSO — sem cota/rede)**: prova o
  que o exit code não permite: `authentication required` (em stderr, exit 0) →
  `Failed(executor.authentication_required)`; negação de permissão headless →
  `Failed(executor.tool_permission_denied)`; saída vazia → `Failed(executor.no_output)`; JSON
  de veredito → `Completed` e `CriticReviewContract` fecha o veredito.
- **`ExternalAgentRealExecutionSmokeTests.AntigravityCriticSmoke…` (1, live, opt-in)**: roda o
  `agy` REAL no perfil isolado. Sem login isolado, declara `BLOCKED_EXTERNAL_OAUTH`.

Regressão: UnitTests 266/266, ContractTests 41/41, ArchitectureTests 7/7, build Release
0 warnings. Claude/Codex inalterados (o `PromptDelivery` padrão continua STDIN).

## Login isolado — BLOCKED_EXTERNAL_OAUTH

O `agy` autentica por OAuth interativo; o perfil isolado nunca herda o login global. O smoke
live (opt-in) produziu, de verdade:

```
BLOCKED_EXTERNAL_OAUTH: perfil worker-antigravity-review sem login isolado
(executor.authentication_required).
Login humano único: HOME=$HOME/.harness/accounts/worker-antigravity-review/config agy
```

**Comando EXATO de login isolado** (ação humana única, OAuth por navegador):

```bash
HOME="$HOME/.harness/accounts/worker-antigravity-review/config" agy
```

Após esse login, o smoke live passa a responder de verdade e o critic Antigravity fica
disponível para revisão live. Até lá, o Piloto 2 usa o critic fallback `worker-codex-critic`
e só pode declarar **GO concorrente técnico**, nunca GO concorrente completo.

## Limitações honestas

- O prompt do `agy` vai no argv (visível a `ps` na máquina local); é limitação do binário,
  mitigada pelo guard de segredo. Prompts muito grandes podem exceder o `ARG_MAX` local.
- `agy --print` não expõe `sessionId` nem `usage` no stdout; ambos ficam `null` (honesto,
  nunca inventado). A retomada por `--conversation` depende de um ID conhecido externamente.
