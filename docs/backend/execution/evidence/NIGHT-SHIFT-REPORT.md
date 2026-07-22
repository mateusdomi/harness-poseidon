# Turno noturno — relatório

Data: 2026-07-22. Branch: `develop`. Baseline: `92c35e2`. Operador ausente; execução autônoma.

## Resumo executivo

O objetivo #1 — **fechar o banner de turno bloqueado no caminho HTTP real** — foi
**atingido e PUBLICADO**. Após cinco continuations governadas reais (três no turno
noturno + a quarta autorizada e sua conclusão), o banner "Turno registrado, execução
bloqueada" renderiza de verdade, o catálogo i18n dos códigos reais está completo e as
fixtures foram alinhadas ao código canônico do backend. A suíte frontend ficou
**integralmente verde** (incl. `test:e2e:real` 3/3), o **critic independente deu PASS**, e
o trabalho foi **publicado em `develop`** (commit `6825bbd`, autoria `worker-codex-frontend`
preservada). `poseidon start` passou pelo caminho oficial e restart/recovery foi
comprovado. **Go/No-Go do Chief: GO LIMITADO para tarefa frontend.** A instalação
`~/Poseidon-RC3-Usuario` não foi tocada.

## N0 — preflight

`develop=92c35e2`, working tree limpa, só `main`/`develop`, RC3 preservado. `doctor`:
`worker-codex-frontend` (codex-cli 0.144.6) e `chief-claude-primary` (Claude Code 2.1.217)
autenticados; demais aliases não.

## N1 — banner HTTP real (3 continuations governadas)

Cada rodada foi uma continuação governada real (`resumeFromAttemptId`), com nova tentativa,
claims `frontend/**`+`docs/frontend/**`, fencing, worktree de `origin/develop`, patch anterior
aplicado, worker Codex real e critic Claude independente. Gates (incl. `test:e2e:real` contra
Host efêmero) executados pelo operador **antes** do critic.

| Rodada | Attempt | Resultado | Motivo |
|---|---|---|---|
| r1 | `01KY3PFD3A8YF703Y830T41EW6` | fail | worker parou por orçamento sem alterar arquivos (só diagnóstico) |
| r2 | `01KY3PS50NZS4AWM1H04RWYT69` | fail | banner passou a renderizar (estado `blockedTurn`), mas import `act` ausente (typecheck) e i18n cru |
| r3 | `01KY3QHZ5T212M0HMQTT91HES1` | fail | **banner corrigido** (`chat.turnBlocked` movido de `workflows`→`chat`; `act` importado); `test:e2e:real` desktop **verde** |

**Diagnóstico conduzido pelo backend** (leitura, sem editar frontend): o cliente HTTP real
já retornava o body `202 state=blocked`; o banner dependia de `sendMessage.data` (transitório)
e não sobrevivia aos eventos SignalR — r2 corrigiu com estado `blockedTurn` estável. O texto
aparecia como chave crua porque `turnBlocked` estava sob `workflows`, não `chat` — defeito
**latente desde o Piloto 1B** (só `http-real.spec.ts` assere esse texto; os specs mock não),
corrigido em r3.

**Resíduo após as 3 continuations** (verdict r3 `fail`):
- **P1** `banner-action-i18n-key-mismatch`: os `nextAction.code` reais (`workflow.bind`,
  `provider.connectAccount`, `model.enable`, `chief.configureModel`) e `blocker.code`
  (`provider_account.missing`, `model.none_chat_enabled`, `chief.model_unresolved`) não têm
  chave i18n específica em `chat.turnBlocked.actions|blockers` (caem no `unknown`); o teste
  `chat.test.tsx:226` assere um link rotulado "workflow" → `/workflows` e falha. `npm run
  check` fica vermelho por isso.
- **P2** `e2e-real-red-2of3`: os projetos `tablet-light`/`mobile-360-dark` deram timeout de
  sessão por contenção — três projetos de viewport dividindo **um** Host efêmero. Não é
  defeito de código; corrigido no driver com `--workers=1` (execução serial).

Gates r3: `npm ci` 0, `test:e2e` (mock) **0**, `test:a11y` **0**, `test:e2e:real` desktop
**verde** (tablet/mobile timeout por contenção), `npm audit` 0; `npm run check` **1** (teste
unitário acima). SHA-256 de `openapi.json`/`events.json` 1.2 recomputados e batem.

**Decisão de protocolo:** três continuations governadas foram executadas (o limite
autorizado). Como o resíduo é P1 e a suíte não está integralmente verde, **não se publica**.
O trabalho de cada rodada está arquivado em `~/.harness/pilots/` (patch + verdict + gates).

## Quarta e quinta continuations, publicação e GO limitado (autorizadas)

Com a quarta continuation explicitamente autorizada (e a autorização valendo para
"fechar os resíduos dessa cadeia"), a cadeia foi levada até o `pass` e à publicação.

| Rodada | Attempt | Resultado |
|---|---|---|
| r4 (retries) | `01KY4KXXVHBHKT0CGSSQ9DTFBZ` | worker completou o catálogo i18n dos códigos reais e alinhou `chat.test.tsx`; `npm run check`, `build`, `build-storybook`, `test:e2e` (mock) e `test:a11y` **verdes**. Critic reprovou por um resíduo mais fundo: fixtures de turno bloqueado usavam `workflow.link`, mas o backend emite `workflow.bind`. |
| r5 | `01KY4MV452WV95HSRNNPFG3ETJ` | worker alinhou as fixtures ao código canônico `workflow.bind` (preservando o `workflow.link` legítimo do contexto de atividade). **Suíte integral verde**, incluindo `test:e2e:real` **3/3** (cada viewport no seu próprio Host efêmero). Critic **PASS** (`chief-claude-primary`, zero P0/P1). |

Duas retries transitórias do worker foram necessárias em r4 (o Codex às vezes inventa
uma "condição de parada" pela branch `task/agent-run-*`); resolvidas com uma linha de
autorização explícita no prompt. A causa raiz do E2E real 2/3 era do **driver** (três
projetos de viewport num único Host efêmero) — corrigido dando a cada projeto seu
próprio Host fresco, serial, sem reduzir cobertura.

**Publicação governada:** o diff aprovado (só `frontend/**`+`docs/frontend/**`, aplica
limpo em `develop`, não stale) foi integrado com autoria preservada do
`worker-codex-frontend` — commit **`6825bbd`**. Gate pós-integração `build-frontend.sh`
**verde**; backend build verde; format, secrets e governança verdes; push fast-forward.

**`poseidon start`** (§6): **verde** pelo caminho oficial (`POSEIDON_START_EXIT=0`,
"Poseidon disponível") — a publicação destravou o `build-frontend.sh`, sem mudança de
backend, exatamente como diagnosticado.

**Restart/recovery** (§7): comprovado — teste de recovery SQLite (SIGKILL real após 3/6
checkpoints → reinício → reconciliação → 6/6, sem duplicação), 8 testes de integração de
agent-run (claim/lease/fencing adquiridos e liberados, cleanup) e as 5 execuções reais do
piloto (zero órfão em cada). O `RecoverAsync` libera leases expiradas e locks de conta.

**Go/No-Go do Chief: GO LIMITADO para tarefa frontend.** Comprovado: worker real, critic
`pass`, frontend publicado em `develop`, gates integrais verdes, `poseidon start` verde,
restart/recovery comprovado, claims/leases liberados, zero segredo, zero órfão, working
tree limpa, RC3 intacto. **Não** declarado GO concorrente multiagente ainda.

## N3–N7 — próximas fatias (pós-GO limitado)

Com o GO limitado declarado, as fatias seguintes ficam habilitadas e serão perseguidas em
commits independentes: **N3** adapter Antigravity first-class (critic preferencial, profile
isolado, read-only, Default-FAIL; smoke live pendente se faltar OAuth); **N4**
scheduler/quotas/fallback e Piloto 2 (Kimi instalado sem cota = `QuotaLimited`; live
pendente sem autenticação); **N5** backlog P1 do Golden Path (workflow recomendado,
definições built-in, auto-key, catálogos, effort binding, import/export, activity
metadata); **N6** package gate + Poseidon.app; **N7** Central de Entregas e Architecture
Hub (só após N5 e package gate). Skips externos (login/infra) são declarados honestamente.

## Higiene

Todas as worktrees transitórias removidas; branches transitórias apagadas (só `main`/`develop`);
claims/leases liberados; **zero processo órfão**; working tree limpa; commits pushed; RC3
intacto. Artifacts de cada rodada (patch + verdict + gates) em `~/.harness/pilots/`;
o diff publicado em `01KY4MV452WV95HSRNNPFG3ETJ.PUBLISHED.diff`.

## Próximo passo exato

**Piloto 1 fechado com GO limitado.** Seguir com N3 (adapter Antigravity), depois N4
(scheduler/quotas + Piloto 2), N5 (backlog P1) e N6 (package gate), cada um em commits
independentes com gates e evidência; Central de Entregas/Architecture Hub (N7) só depois de
N5 e do package gate, salvo bloqueio externo. Não declarar ainda GO concorrente multiagente.
