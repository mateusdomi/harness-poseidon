# CA-4 — adapters reais de executores externos (Claude Code e Codex)

Data: 2026-07-21. Branch: `develop`. Continuação de ADR-021, sobre os perfis de CA-3.

## Escopo desta fatia

Escopo mínimo e deliberado: os DOIS executores necessários ao Piloto 1 — `claude-code`
(Chief) e `codex` (worker de frontend). Kimi, GLM e Antigravity entram depois do Piloto,
conforme a ordem obrigatória. O adapter do Claude Code já serve o GLM, que é o mesmo
binário apontado a endpoint compatível.

## Interface

`IExternalAgentExecutor` (probe, start) + `IExternalAgentSession` (stream, collect, cancel,
stop, cleanup, sessionId, processId, isRunning). `ProcessExternalAgentExecutor` concentra o
que é comum: probe, montagem do ambiente pelo perfil isolado, recusa de segredo em argv,
entrega do prompt e criação da sessão.

Contratos: `ExternalAgentRunRequest`, `ExternalAgentEvent` (`Started`, `Delta`, `ToolUse`,
`Usage`, `Quota`, `Completed`, `Failed`), `ExternalAgentUsage`, `ExternalAgentRunResult`,
`ExternalAgentRunStatus` (`Completed`, `Failed`, `Cancelled`, `TimedOut`) e
`ExternalAgentAccess` (`ReadOnly`, `Workspace`).

## Nenhuma flag inventada — o que foi OBSERVADO

Comandos e formatos foram observados nas CLIs instaladas nesta máquina, executando-as:

### Claude Code 2.1.216

```text
claude -p --output-format stream-json --verbose \
  [--resume <sessionId>] [--model <m>] [--effort <low|medium|high|xhigh|max>] \
  [--permission-mode acceptEdits | --tools Read,Grep,Glob --permission-mode dontAsk] \
  [--add-dir <dir>]
```

Saída real observada, uma linha JSON por evento:

- `{"type":"system","subtype":"init", ... "session_id":"…"}` → identidade da sessão;
- `{"type":"assistant","message":{"content":[{"type":"text","text":"…"}]}}` → texto;
- `{"type":"rate_limit_event","rate_limit_info":{"status":"allowed", …}}` → sinal de COTA;
- `{"type":"result","subtype":"success","is_error":false,"result":"…","total_cost_usd":…,`
  `"usage":{"input_tokens":…,"output_tokens":…},"num_turns":…}` → resultado, custo, consumo.

Correção de honestidade: `--effort` da CLI instalada aceita `low|medium|high|xhigh|max`. O
catálogo declarava apenas três níveis; passou a declarar os cinco reais, e um valor fora da
lista é recusado com `executor.effort_unsupported` em vez de chegar à CLI.

### Codex CLI 0.144.6

```text
codex exec [resume <threadId>] --json --skip-git-repo-check \
  -C <workdir> --sandbox <workspace-write|read-only> \
  --output-last-message <arquivo> [-m <modelo>] [--add-dir <dir>] -
```

Saída real observada (JSONL):

- `{"type":"thread.started","thread_id":"…"}` → sessão;
- `{"type":"item.completed","item":{"type":"agent_message","text":"…"}}`;
- `{"type":"turn.completed","usage":{"input_tokens":…,"cached_input_tokens":…,`
  `"output_tokens":…,"reasoning_output_tokens":…}}`.

`--output-last-message` é a fonte AUTORITATIVA da resposta final; o stream serve a
progresso. O Codex CLI não publica custo, então `CostUsd` fica **nulo** — desconhecido,
nunca zero inventado.

## Actor × critic vira configuração de processo, não pedido

`ExternalAgentAccess` é o único ponto onde a diferença existe, e ela é estrutural:

- Codex actor → `--sandbox workspace-write`; Codex critic → `--sandbox read-only`.
- Claude actor → `--permission-mode acceptEdits`; Claude critic → `--tools Read,Grep,Glob`,
  isto é, sem `Edit`, `Write` ou `Bash` na lista. O critic não pode escrever porque a
  ferramenta não existe na sessão — não porque foi pedido que não escrevesse.

## Segurança

- O prompt vai por **stdin** (`-` no Codex, stdin no `claude -p`), nunca em argv: em
  argumento ele apareceria na tabela de processos e em qualquer captura de comando.
- O ambiente do subprocesso é **zerado** e remontado pela allowlist do perfil isolado.
- Qualquer argumento que aparente segredo aborta o start com `executor.secret_in_arguments`.
- Toda saída passa por `ExternalAgentRedaction` antes de virar evento, delta ou resultado.
  De itens que não são mensagem do agente sai apenas o TIPO, nunca o conteúdo.
- `StopAsync` mata a **árvore** de processos; `DisposeAsync` sempre para e limpa, então
  nenhuma sessão deixa processo órfão.
- O canal de eventos ao vivo é limitado (`DropOldest`): um consumidor lento não faz o Host
  crescer sem limite. O registro autoritativo é o resultado acumulado, não o canal.

## Provas executadas

`tests/Harness.UnitTests/Agents/ExternalAgentExecutorTests.cs` — 13 testes, sem consumo de
cota: argumentos exatos de actor/critic nas duas CLIs, retomada pelo subcomando real do
Codex e por `--resume` do Claude, prompt fora do argv, GLM compartilhando binário mas nunca
o config home, effort não suportado recusado, perfil de outro alias recusado, working
directory inexistente recusado, e redaction que remove segredo sem estragar texto comum.

`tests/Harness.IntegrationTests/Agents/ExternalAgentRealExecutionSmokeTests.cs` — smoke de
execução REAL, opt-in por `HARNESS_RUN_REAL_AGENT_TESTS=true`.

### Estado observado nesta máquina

O isolamento de CA-3 foi comprovado por execução real: com `CLAUDE_CONFIG_DIR` apontado ao
perfil da conta, a CLI respondeu `Not logged in · Please run /login` com
`is_error: true` e **zero token consumido**. Isso confirma que o config home isolado é uma
identidade separada de verdade, e não uma pasta cosmética que herda a sessão do operador.

Consequentemente o smoke registrou, para os dois executores:

```text
SKIPPED_EXTERNAL_CREDENTIALS: perfil chief-claude-primary não autenticado ou sem cota para claude-code.
SKIPPED_EXTERNAL_CREDENTIALS: perfil worker-codex-frontend não autenticado ou sem cota para codex.
```

O smoke distingue "não autenticado" de "falhou" por sinal ESTRUTURAL — nenhum token entrou
nem saiu, logo o modelo não foi alcançado — e não por casar texto de erro, que muda entre
versões da CLI. **Nenhum sucesso real é declarado sem execução real.**

### Bloqueio operacional registrado

O Piloto 1 exige que cada alias seja autenticado UMA vez no seu próprio config home. Essa é
a "autenticação inicial previamente configurada" prevista, e é ação humana: o backend não
copia, não deriva e não persiste credencial de conta.

## Gates

Build Release 0 avisos/0 erros; `dotnet format --verify-no-changes` limpo; governança
sync/generate/lint verde; scan de segredos limpo. Nenhum arquivo em `frontend/**` ou
`docs/frontend/**` foi modificado.

## Próxima fatia

CA-5 — bootstrap governado (`task`, `attempt`, account lease, claims, worktree isolada,
context bundle, receipt, lease renewal, eventos, resultado, cleanup) sobre estes adapters.
