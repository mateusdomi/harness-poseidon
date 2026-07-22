# Piloto 2 concorrente — No-Go, BLOCKED_EXTERNAL_OAUTH

Data: 2026-07-22. Branch: `develop`. Base: `cf3a82e` (N3+N4). Não simulado.

## Passo 1 — `./poseidon agent doctor` (real, sobre os perfis isolados)

Host iniciado pelo caminho oficial (`./poseidon start`) com `Harness:AgentRuns:Enabled=true`
e `ControlledRoot=~/Documents`. O doctor probou cada conta (probe `--version` + material de
autenticação presente no config home isolado, sem gastar cota):

| alias | executor | adapter | instalado | perfil ok | **autenticado** |
|---|---|---|---|---|---|
| chief-claude-primary | claude-code | ✓ | ✓ (2.1.217) | ✓ | **true** |
| worker-codex-frontend | codex | ✓ | ✓ (0.144.6) | ✓ | **true** |
| worker-antigravity-review | antigravity | ✓ | ✓ (1.1.5) | ✓ | **false** |
| worker-claude-secondary | claude-code | ✓ | ✓ (2.1.217) | ✗ (não provisionado) | **false** |
| worker-codex-critic | codex | ✓ | ✓ (0.144.6) | ✗ (não provisionado) | **false** |
| worker-glm-general | glm | ✓ | ✓ | ✗ | **false** |
| worker-kimi-ui | kimi-code | ✗ (sem adapter) | ✗ | ✗ | **false** |

Confirmado também por probe direto das CLIs nos perfis isolados:

- `worker-codex-frontend` (frontend actor): turno Codex real respondeu `pong` — **autenticado**.
- `worker-claude-secondary` (backend actor): Claude respondeu `Not logged in · Please run /login`
  — **não autenticado** (login interativo humano).
- `worker-antigravity-review` (critic): `authentication required` — **BLOCKED_EXTERNAL_OAUTH**
  (ver `N3-ANTIGRAVITY-CRITIC.md`).

## Por que o Piloto 2 concorrente NÃO pode ser executado agora

O Piloto 2 exige DOIS actors autenticados executando em paralelo mais um critic
INDEPENDENTE (conta distinta dos actors). O inventário autenticado é insuficiente:

- **Backend actor `worker-claude-secondary`: não logado.** A fatia backend não roda live.
- **Critic `worker-antigravity-review`: OAuth bloqueado.** Critic fallback
  `worker-codex-critic`: também não provisionado/autenticado.
- Contas autenticadas ao todo: **apenas 2** — `chief-claude-primary` (orquestrador, papel
  `chief-orchestrator`) e `worker-codex-frontend` (frontend). Não há dois WORKER-actors
  autenticados, nem uma terceira conta autenticada para o critic independente.
- As próprias regras do scheduler N4 confirmam o bloqueio, não o contornam: `chief` seria
  recusado como backend actor (`account.role_not_allowed`), e usar as 2 contas autenticadas
  como actors deixaria zero conta para o critic (`actor_cannot_be_critic` / sem elegível).

Conforme a regra absoluta da missão — "não simule a execução" — **nada foi simulado**. Não
se declara GO concorrente (nem completo, nem técnico), porque a concorrência de dois actors
com critic independente não pôde ser exercida de verdade.

**Veredito: No-Go concorrente. Bloqueio externo: OAuth/login humano.**

## Ação humana única para desbloquear (comandos exatos, login isolado)

```bash
# Backend actor (Claude Code, login OAuth interativo no config home isolado)
CLAUDE_CONFIG_DIR="$HOME/.harness/accounts/worker-claude-secondary/config" claude   # depois /login

# Critic Antigravity (OAuth interativo no HOME isolado)
HOME="$HOME/.harness/accounts/worker-antigravity-review/config" agy

# (Opcional) critic fallback Codex, se preferir não usar o Antigravity live
CODEX_HOME="$HOME/.harness/accounts/worker-codex-critic/config" codex login
```

Depois desses logins, `./poseidon agent doctor` mostra `authenticated: true` para as contas,
e o Piloto 2 concorrente pode ser executado de verdade (dois actors simultâneos + critic
independente + restart/recovery).

## Higiene

Host encerrado (`./poseidon stop`); zero processo `Harness.Host/Runner/Launcher` órfão;
somente `main` e `develop`; nenhuma worktree transitória; working tree limpa; RC3 intacto.
Nenhuma cota consumida além dos probes mínimos de autenticação.

## Trabalho independente entregue nesta rodada (não bloqueado)

Como o Piloto concorrente está bloqueado por OAuth externo, a fatia P1 independente
sugerida pela missão foi entregue diretamente e de forma governada: **auto-key versionado de
`agent_key`** (ver `AUTO-KEY-AGENT-DEFINITIONS.md`).
