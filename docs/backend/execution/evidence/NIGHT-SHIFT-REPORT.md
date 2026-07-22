# Turno noturno — relatório

Data: 2026-07-22. Branch: `develop`. Baseline: `92c35e2`. Operador ausente; execução autônoma.

## Resumo executivo

O objetivo #1 — **fechar o banner de turno bloqueado no caminho HTTP real** — foi
**substancialmente atingido**: o banner "Turno registrado, execução bloqueada" passou a
renderizar de verdade e `test:e2e:real` (projeto desktop) passa. O trabalho **não foi
publicado** porque `npm run check` permanece vermelho por um teste unitário e um resíduo de
i18n de rótulos de ação — depois de esgotadas as **três** continuations governadas
autorizadas. Verdict final: **fail (P1 restante)** → **No-Go / nada publicado** (bloqueio
correto). A instalação `~/Poseidon-RC3-Usuario` não foi tocada.

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

## N2–N7 — dependem da publicação de N1

`poseidon start`: a causa do drift é o **próprio `build-frontend.sh`** (rodado por
`resolve_launcher`), que gateia em `npm run check`; com a suíte frontend vermelha, `poseidon
start` aborta. **Publicar o fix de N1 destrava `poseidon start` pelo caminho oficial** — nenhum
código de backend precisa mudar. Confirmado que `events.json` já está em 1.2. Restart/recovery
e GO limitado (N2), Antigravity (N3), scheduler/Piloto 2 (N4), backlog P1 (N5), package
gate/Poseidon.app (N6) e módulos novos (N7) dependem de N1 publicado e permanecem
**pendentes**, não iniciados nesta rodada para não abrir frente grande sem fechar a #1.

## Higiene

Todas as worktrees transitórias removidas; branches transitórias apagadas (só `main`/`develop`);
claims/leases liberados; **zero processo órfão**; working tree limpa; commits pushed; RC3
intacto.

## Próximo passo exato

Uma continuation adicional (4ª — requer autorização, pois excede o limite de 3 desta rodada)
com `resumeFromAttemptId=01KY3QHZ5T212M0HMQTT91HES1`, instrução mínima: adicionar as chaves
i18n de `chat.turnBlocked.actions`/`blockers` para os `code` reais do backend (mapeando
`workflow.bind`→"Vincular workflow" com rota `/workflows`, etc.) e alinhar `chat.test.tsx:226`.
Com `npm run check` verde e `test:e2e:real` serial verde, o critic tende a `pass` → publicar em
`develop` → `poseidon start` (destravado) → restart/recovery → **GO limitado do Chief** →
seguir N3–N7.
