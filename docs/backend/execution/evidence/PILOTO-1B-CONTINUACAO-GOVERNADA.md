# Piloto 1B — continuação governada de um attempt reprovado

Data: 2026-07-21. Branch: `develop`. Continuação de [Piloto 1A e CA-7](PILOTO-1A-E-CA-7-CRITIC.md).

O critic reprovou o Piloto 1A com verdict `fail` e 9 achados; o trabalho do worker ficou
**fora de `develop`**, arquivado como patch em `~/.harness/pilots/`. A retomada exigia uma
primitiva que o bootstrap não tinha: continuar um attempt reprovado **sem** expor uma
`baseReference` Git arbitrária. Esta fatia entrega essa primitiva.

## A primitiva: `resumeFromAttemptId`, não um ref Git

O contrato de entrada ganhou `resumeFromAttemptId` — o id **durável** da tentativa anterior.
O cliente **não** fornece base Git: a nova worktree nasce da referência interna governada
(`origin/develop` atual), resolvida pelo servidor. O teste de drift comprova que o schema
publicado expõe `resumeFromAttemptId` e **não** expõe `baseReference` nem `scopeClaims`.

## Arquivo governado de artifacts (`AttemptArtifactArchive`)

O patch reprovado é preservado fora do repositório com um **manifest de provenance** que o
amarra a tudo que a continuação precisa validar:

- `patchSha256` — checksum do conteúdo bruto do patch;
- `receiptTurnId` e `reviewId` — receipt de governança e review do critic que reprovou;
- `verdict` — só se continua o que foi `fail`;
- `attemptId`, `tenantId`, `projectId`, `taskId`, `role`, `actorAlias` — identidade;
- `controlledRepositoryRoot` — o repositório onde o trabalho foi produzido;
- `sourceCommit` — commit do worker (identidade do actor);
- `scopeClaims` e `findings` — escopo autorizado e achados do critic.

`TryLoad` recomputa o SHA-256 do patch e o compara ao manifest. Qualquer inconsistência é um
código de recusa **tipado**, nunca um patch confiado às cegas:
`archive.manifest_missing`, `archive.manifest_invalid`, `archive.schema_unsupported`,
`archive.patch_path_invalid`, `archive.patch_missing`, `archive.checksum_mismatch`.

O manifest é o único registro durável necessário — não depende de uma linha de banco que
possa ter sido descartada (a instalação do Piloto 1A não tem banco persistente).

## Política de continuação (`ContinuationPolicy`)

Pura, determinística e Default-REFUSE. Recusa com código tipado quando:

| Código | Motivo |
|---|---|
| `continuation.verdict_not_fail` | reabrir trabalho aprovado seria refazê-lo à toa |
| `continuation.project_mismatch` | projeto diferente |
| `continuation.task_mismatch` | tarefa diferente |
| `continuation.role_mismatch` | papel diferente |
| `continuation.actor_mismatch` | outra conta; continuação não troca de dono |
| `continuation.repository_mismatch` | patch não é portável entre árvores arbitrárias |
| `continuation.scope_escalation` | o arquivo reivindicava claim que o papel não concede |

Autorizada, ela transforma os achados do critic em **critérios de aceite** da nova tentativa.

## Aplicação controlada e detecção de stale

Na worktree nova (a partir de `origin/develop`), `GitWorktreeManager.TryApplyPatchAsync`
faz `git apply --check` antes de aplicar. Se o patch não casa mais com a base — a árvore
mudou — ele é **stale**: nada é forçado, a worktree não fica meio aplicada, e o prompt do
worker recebe o diff anterior apenas como **CONTEXTO**, com a instrução explícita de
reconstruir sobre a base atual. Nada de best effort silencioso.

Quando aplica limpo, o worker continua sobre o trabalho anterior já materializado, com os 9
achados como critérios a fechar.

## O que a continuação cria

Uma tentativa **nova** (nunca reusa a anterior), com **novos claims**, **novo fencing**,
**nova worktree**, **novo context bundle** e **novo receipt**. A proveniência é preservada:
`resumeFromAttemptId`, `sourceCommit`, `reviewId` e `receiptTurnId` viajam no contexto.

## Prova

- **Unit** (`AttemptArtifactArchiveTests`, `ContinuationPolicyTests`): roundtrip write/load
  com checksum selado; patch adulterado → `archive.checksum_mismatch`; manifest/patch
  ausentes → códigos tipados; escape de diretório recusado; cada mismatch de identidade e a
  escalada de escopo com o código exato; verdict `pass` não continua.
- **Contract drift** (`AgentRunContractDriftTests`): `resumeFromAttemptId` presente,
  `baseReference` e `scopeClaims` ausentes no schema publicado.
- **Integração contra o Host real** (`AgentRunContinuationTests`): sobre um repositório Git
  real com `origin/develop`, o attempt anterior é levado ao estado terminal **reprovado**
  (completed → review independente `rejected` → instrução v2), arquivado com manifest; a
  retomada devolve `202` com uma tentativa **nova** (id distinto), claims do papel e fencing
  positivo, worktree nascida de `origin/develop`, receipt gravado. Um patch adulterado por
  fora é recusado com `409 archive.checksum_mismatch`.

## Estado dos gates

- **Backend**: build Release 0 avisos/0 erros; `dotnet format --verify-no-changes` limpo;
  todos os testes de backend executáveis nesta máquina verdes (Unit 259, Contract 41,
  Architecture 7, Concurrency 3, e as suítes de integração SQLite, incluindo as duas novas
  de continuação). As suítes que exigem **Docker** e **PostgreSQL** não foram exercidas
  porque nem Docker nem PostgreSQL estão de pé nesta sessão — ausência de infraestrutura
  declarada, não regressão. Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi
  alterado por esta fatia.
- **Frontend**: a suíte permanece **vermelha** — `agentRun.stateChanged` (CA-5) e os
  eventos do lifecycle C2/C3 ainda não têm schema no catálogo do frontend. É exatamente o
  achado P0 `suite-vermelha` que a continuação existe para fechar, pelo worker governado,
  não pelo backend.

## Execução real (Piloto 1B live)

A continuação foi executada de verdade, com o Codex e o Claude autenticados, sobre o
repositório real. Driver reproduzível (gated por `HARNESS_RUN_LIVE_PILOT=true`) em
`tests/Harness.IntegrationTests/Agents/LiveContinuationPilotDriver.cs`.

- **Autenticação real** (`doctor`): `worker-codex-frontend` (codex-cli 0.144.6) e
  `chief-claude-primary` (Claude Code 2.1.217) autenticados; os demais aliases não.
- **Continuação disparada**: attempt anterior semeado e levado a `rejected` pela cadeia real;
  patch do Piloto 1A arquivado com manifest (sha256 validado); `resumeFromAttemptId` devolveu
  `202` com **nova tentativa** `01KY3M2BFEGN32MV0FEW61B4EF`, claims `frontend/**` +
  `docs/frontend/**`, fencing 1/1, worktree nascida de `origin/develop`, patch aplicado limpo.
- **Worker real** (~5 min): fechou **7 dos 9 achados** — o critic confirma migração de
  `chiefTurnState` fechada, **estado inicial honesto** (`default null → notStarted`, o defeito
  classe-RC3), códigos com i18n, readiness consumido, banner limpo entre conversas e
  `disabled` sem hardcode. Honesto no limite: **não fez commit** porque não pôde executar os
  E2E obrigatórios no próprio run.
- **Gates executados na worktree pelo operador**: `npm run check` **verde** (P0
  `suite-vermelha` fechado — os três drift tests passam), `test:e2e` **72/72**, `test:a11y`
  **42/42**, SHA-256 de `openapi.json` (`8dbaca…ab553`) e `events.json` 1.2 (`765a96…b65b16`)
  **recomputados e batem** com o `HANDOFF` (P2 `catalogo-nao-verificavel` fechado).
- **`test:e2e:real` (http-real)**: **1 falha** — após enviar a mensagem contra o Host real (que
  responde `202 state=blocked` com blockers/nextActions tipados, confirmado por curl), a UI
  mostra a região "Execução do chefe bloqueada" e a mensagem persistida, mas **não renderiza o
  banner de confirmação `Turno registrado, execução bloqueada`** no caminho HTTP real. O mock
  (`test:e2e`) passa; o real não. É um gap de integração verdadeiro que só o E2E real pega.
- **Critic real e independente** (`chief-claude-primary`, conta/executor distintos do actor):
  **verdict `fail`** (`reviewId 01KY3MC86QS24K9NNMNRQFF41T`), agora reduzido a P2 —
  `e2e-sem-evidencia` (correto: o E2E real reprova) e `catalogo-nao-verificavel` (limitação do
  critic read-only, resolvida pela recomputação do operador). Zero P0/P1.

**Go/No-Go do Chief: No-Go.** A máquina de continuação funcionou de ponta a ponta e o worker
fechou os defeitos de código, incluindo o estado inicial desonesto. Mas a **suíte frontend não
está integralmente verde** (`http-real` reprova o banner de turno bloqueado no caminho real),
então o verdict é `fail` e **nada foi publicado em `develop`** — o bloqueio correto. Higiene:
worktree removida, branch da tentativa apagada, claims/leases liberados, **zero processo
órfão**, working tree limpa. O trabalho da continuação foi arquivado fora do repositório
(`~/.harness/pilots/01KY3M2BFEGN…`), como manda o padrão de tentativa reprovada.

## Próximo passo exato

Nova rodada de continuação (`resumeFromAttemptId=01KY3M2BFEGN…`) para fechar o único achado de
código restante: fazer o caminho HTTP real renderizar o banner `Turno registrado, execução
bloqueada` ao receber `202 state=blocked` (hoje só o mock o faz). Re-executar todos os gates,
incluindo `test:e2e:real`, e o critic. Só com verdict `pass` e suíte integral verde, publicar
em `develop` e então exercer `poseidon start`, restart/recovery e o GO limitado do Chief.
