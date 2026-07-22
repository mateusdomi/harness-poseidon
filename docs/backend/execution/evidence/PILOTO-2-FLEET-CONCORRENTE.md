# Piloto 2 — fleet concorrente real: GO concorrente multiagente com Antigravity LIVE (Parte 1)

Data: 2026-07-22. Branch: `develop`. Base: `4cbb367`. Executado de verdade (4 runs reais).

## Resultado — GO concorrente multiagente (múltiplas CONTAS distintas)

O chefe (orquestrador) despachou **três actors distintos SIMULTANEAMENTE** em subárvores
disjuntas, com critic Antigravity **LIVE**, provado por execução real (driver
`FleetConcurrencyPilotDriver`, opt-in `HARNESS_RUN_FLEET_PILOT=true`):

| Prova | Resultado |
|---|---|
| Execução concorrente | **maxConcurrent = 3** (3 actors rodando ao mesmo tempo) |
| Backend GLM (`worker-glm-general`) | **Completed** — criou o probe no seu claim |
| Backend Claude (`worker-claude-secondary`) | **Completed** — criou o probe no seu claim |
| Frontend Codex (`worker-codex-frontend`) | **Completed** — criou o probe no seu claim |
| Isolamento | 3 attempts / branches / worktrees / claims **disjuntos** |
| Critic Antigravity **LIVE** | veredito real tipado: `fail` em diff vazio (3º run), `pass` em trabalho real (4º run) |
| Restart/recovery | reconciliação sem duplicação (`recovered: []`) |
| Higiene | zero probe vazou ao repo principal; worktrees/branches transitórias removidas |

Veredito final do critic Antigravity (4º run, trabalho real, executor real 7,3s):
> `pass` — "O critério de aceite foi plenamente atendido. O arquivo
> docs/backend/execution/evidence/fleet-glm/probe.md foi criado contendo exatamente uma
> linha, dentro do escopo autorizado." (0 findings)

O critic é HONESTO: reprovou (`P1 CRITERIA_NOT_MET`) quando recebeu diff vazio e aprovou
quando recebeu o trabalho real — não é pass cego.

## Quatro achados reais corrigidos ao longo dos runs (o valor do teste)

1. **GLM "Not logged in" no driver** — o `dotnet test` não carrega `~/.harness/glm.env`.
   Corrigido: o driver resolve o token do Keychain (`poseidon-glm-general`) para o ambiente.
2. **Codex se recusou "governança exige só develop"** — o worker leu o `CLAUDE.md` na
   worktree e não trabalhou. Corrigido: o prompt do worker (`BuildPrompt`) explicita que a
   worktree de tentativa É o modo governado correto.
3. **Antigravity critic `tool_permission_denied`** — `--mode plan --sandbox` sem
   skip-permissions auto-nega qualquer tool. Corrigido: critic usa
   `--mode plan --dangerously-skip-permissions` (lê, mas `plan` impede escrita).
4. **RAIZ do Antigravity (também explicava o N3 "blocked"): `--print` CONSOME o próximo
   argumento como o prompt.** O adapter punha `--print` no início, engolindo o flag seguinte
   como "prompt" (o modelo respondia sobre os flags). Corrigido: `--print` é o ÚLTIMO flag e
   o prompt posicional vira o seu valor. Confirmado por probe: com `--print` por último o
   Antigravity retorna JSON limpo.

## Limite honesto — Parte 2 pendente (múltiplas instâncias da MESMA conta)

Provado: **múltiplas CONTAS distintas** concorrentes. **Múltiplas INSTÂNCIAS da mesma conta**
ainda não: o `AccountProfileProvisioner.AcquireLock` é single-owner por alias (o config home
tem lock exclusivo). Habilitá-lo exige um lease multi-owner do perfil (até o
`concurrencyLimit` da conta) — próximo desenvolvimento (gap 3), com o config home
compartilhado em leitura e o worktree exclusivo por instância.

## Testes / regressão

Driver `FleetConcurrencyPilotDriver` (opt-in). Adapter/prompt cobertos por
`AntigravityExternalAgentExecutorTests` (9, unit — `--print` por último, critic plan sem
edição). Regressão verde: UnitTests 302/302, format limpo, build Release 0 warnings. Nada
publicado pelos actors (o piloto não integra em `develop`).
