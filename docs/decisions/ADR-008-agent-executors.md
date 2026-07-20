# ADR-008 — Estratégia de executores de agente

- Status: aceito
- Data: 2026-07-18

## Decisão

`IAgentExecutor` terá `FakeAgentExecutor` determinístico para testes, `CodexCliAgentExecutor` como primeiro executor real e `MicrosoftAgentFrameworkExecutor` para chefe/não codificadores. Testes reais exigem `HARNESS_RUN_REAL_AGENT_TESTS=true`.

O `CodexCliAgentExecutor` usa o protocolo JSON-RPC estável do `codex app-server` sobre stdio. O Host/Runner supervisiona o processo, emite heartbeats próprios e interrompe a árvore exata do subprocesso. Cada tentativa recebe um diretório de estado Codex contido no sandbox e usado exclusivamente como `CODEX_HOME`; configuração, sessões e credenciais pessoais não são herdadas. O ambiente do subprocesso começa por uma allowlist mínima, sem tokens/chaves/segredos.

Retomada por `threadId` é uma otimização quando o rollout persistido está íntegro. A recuperação obrigatória cria uma sessão nova a partir do estado de domínio, instrução imutável, commit Git e hashes de artefatos catalogados. O protocolo experimental não é necessário para esse fluxo.

Referências oficiais verificadas na PoC-4: [Codex app-server](https://developers.openai.com/codex/app-server/) e [modo não interativo/retomada](https://developers.openai.com/codex/non-interactive/).

## Consequências

Automação não consome rede/cota por padrão. A prova automatizada inicia threads e persiste um item de marcador, mas nunca inicia um turno de modelo. Smoke com turno real permanece explicitamente opt-in. Sessão ausente é reidratada por persistência, instrução, Git e artefatos. O quarto executor opcional `OmpRpcAgentExecutor` foi promovido em 2026-07-20 pela missão de governança e é detalhado no ADR-016. Outros adapters permanecem backlog até medição e autorização.
