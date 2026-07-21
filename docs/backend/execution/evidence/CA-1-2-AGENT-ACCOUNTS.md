# CA-1/CA-2 — papel provider-agnostic e registro de contas de agente

Data: 2026-07-21. Branch: `develop`.

Primeiras fatias da ativação do Chief multiagente (ADR-021).

## CA-1 — identidade de frontend provider-agnostic

Causa raiz: o escopo de paths estava amarrado ao provider.
`AgentPathScopeKind` tinha o valor `Kimi` e o Host resolvia o papel por
`KimiAgentDefinitionKeys`. Consequência: o Codex não conseguia exercer o papel de frontend
sem burlar a governança.

Entrega:

- `AgentPathScopeKind.FrontendSpecialist` é o papel canônico; `Kimi` permanece como **alias
  do mesmo valor**, preservando código e configuração existentes.
- `FrontendSpecialistAgentDefinitionKeys` aceita `frontend-specialist`, `frontend-kimi`,
  `kimi`, `kimi-code`, `frontend-codex` e `codex-frontend`. A chave histórica
  `Harness:IsolatedExecution:KimiAgentDefinitionKeys` continua funcionando e prevalece
  quando informada.
- Regra canônica `governance/rules/coordination.md` reescrita: o escopo pertence ao PAPEL,
  nunca ao provider, e trocar o executor **não amplia escopo**.

Provas: backend continua bloqueado em `frontend/**`; o papel de frontend continua limitado a
`frontend/**` e `docs/frontend/**`; o alias histórico resolve para o mesmo escopo; a chave de
configuração antiga ainda sobrescreve a lista.

## CA-2 — registro de contas e executores

- Contratos tipados: `AgentAccountContract`, `ExecutorProfile`, `CredentialReference`,
  `CapabilitySet`, `QuotaSnapshot`, `AgentAccountHealth`, `AccountLeaseContract`,
  `AccountSelectionDecision` e os estados fechados de `AgentAccountState`
  (`Available`, `Reserved`, `Running`, `CoolingDown`, `QuotaLimited`,
  `AuthenticationRequired`, `Degraded`, `Disabled`, `Unavailable`).
- **Nenhum e-mail é aceito**: um alias contendo `@` é recusado com
  `account.alias_must_not_be_email`. A associação alias → conta real vive apenas em
  configuração local protegida.
- **Nenhum segredo é aceito**: o `credentialRef` exige esquema allowlisted
  (`keychain://`, `secret://`, `env://`); um valor cru é recusado com
  `account.credential_reference_must_be_opaque`.
- Reserva de conta com **fencing crescente**: fencing antigo não libera a concessão vigente;
  dupla reserva, limite de concorrência e cota esgotada são recusados por código tipado.

## Probe real dos executores (base de CA-4)

Executado nesta máquina, com ambiente mínimo por allowlist:

```text
probe claude-code: installed=True  version=2.1.216 (Claude Code)
probe codex:       installed=True  version=codex-cli 0.144.6
probe antigravity: installed=True  version=1.1.5
probe kimi-code:   installed=True  version=0.27.0
probe glm:         installed=True  version=2.1.216 (Claude Code)
```

Um executor ausente é reportado como `Unavailable` e nunca como suportado (verificado com um
binário inexistente).

Observações operacionais registradas:

- **Antigravity não é experimental.** A CLI `agy` está instalada e é usada em produção pelo
  operador para code review; foi modelada como executor de primeira classe com vocação de
  critic.
- **GLM usa o mesmo binário do Claude Code** (`claude`), apontado a endpoint compatível por
  variáveis de ambiente. Por isso exige `CLAUDE_CONFIG_DIR` próprio — sem isso duas contas
  sobrescreveriam a autenticação uma da outra. Há teste garantindo esse invariante.
- **Kimi está sem cota** no momento (informação do operador); o probe confirma instalação,
  o que é diferente de disponibilidade de cota — o estado de cota é modelado separadamente
  (`QuotaLimited`).

## Gates executados

- `dotnet build Harness.sln -c Release`: 0 avisos, 0 erros.
- `dotnet format --verify-no-changes`: limpo.
- Suíte integral verde (ver seção de execução do commit).
- Governança: `sync` até `changed=0`, `generate`, `lint --warnings-as-errors` verde.
- `tools/backend/scan-secrets.sh`: limpo.

Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Risco externo registrado

O guia de integração do GLM fornecido ao agente continha um token em texto plano (também
presente em `~/.zshrc` do operador). Esse valor **não foi persistido** neste repositório em
nenhuma forma. Recomendação: rotacionar o token e movê-lo para o Keychain, conforme
`docs/backend/operations/AGENT_ACCOUNTS_EXAMPLE.md`.

## Próximas fatias

CA-3 (isolamento por conta em disco: config home, sessão, worktree, logs, cleanup),
CA-4 (adapters `IExternalAgentExecutor` completos: start/stream/resume/cancel/collect),
CA-5 (bootstrap governado `poseidon agent start`), CA-6 (scheduler), CA-7 (actor/critic) e
CA-8 (Piloto 1 com o Codex no bloqueio frontend atual).
