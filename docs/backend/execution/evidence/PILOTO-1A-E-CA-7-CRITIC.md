# Piloto 1A e CA-7 — worker governado real e critic independente

Data: 2026-07-21. Branch: `develop`. Continuação de ADR-021 (CA-1..CA-5).

## Autenticação isolada validada

`./poseidon agent doctor` com os perfis autenticados pelo operador:

```text
chief-claude-primary      auth=True  profileOk=True  adapter=True   claude-code 2.1.216
worker-codex-frontend     auth=True  profileOk=True  adapter=True   codex-cli 0.144.6
worker-antigravity-review auth=False profileOk=False adapter=False  executor.adapter_not_implemented
worker-codex-critic       auth=False profileOk=False adapter=True
```

Config homes distintos e verificados. Duas correções de honestidade foram necessárias
**antes** de o diagnóstico poder ser confiável:

1. **Falso negativo de autenticação.** O `doctor` procurava `.credentials.json` no config
   home. O Claude Code no macOS guarda o token no **Keychain** e registra a conta vinculada
   em `.claude.json`. Um perfil autenticado era reportado como não autenticado. Passou a
   observar a presença da chave `oauthAccount` — apenas a PRESENÇA; o objeto contém
   identidade real (inclusive e-mail) e seu conteúdo nunca é lido nem propagado.
2. **A allowlist de ambiente quebrava a autenticação.** Isolado por `env -i` + allowlist, o
   Claude Code respondia `Not logged in` mesmo autenticado. Bisecção do ambiente
   (`USER`/`TMPDIR`/`LOGNAME`/`SHELL`) mostrou que **`USER` é obrigatório** para alcançar o
   item do Keychain. `USER` entrou na allowlist dos cinco executores. Sem isso, todo run
   Claude/GLM falharia por "não autenticado" — um defeito que só aparece em execução real.

## Cadeia de domínio real

Criada pelas APIs oficiais, sem SQL e sem ULID inventado:

| Agregado | ID |
|---|---|
| organização | `01KY36JQ7P73ZKYCF5B9BTDPZ7` |
| projeto | `01KY36JQ8A48Q2TVYJMA5Q1N4F` |
| solicitação | `01KY36K2MBSV84H726Y3YWAXSV` |
| demanda | `01KY36K2N1CET71NBJPMYCH08D` |
| tarefa | `01KY36KYHVWB0ZYWCZQPBQZ1ZP` |
| tentativa | `01KY36MZMV5YFDHW9EWFZMK0CM` |

Lacuna fechada nesta rodada: não havia superfície oficial para **iniciar tentativa**.
`poseidon agent start` passou a iniciá-la de verdade pela cadeia de trabalho
(`StartAttemptAsync` com solicitação de origem, instrução corrente e versão esperada da
tarefa) quando `--attempt` é omitido. Isso é diferente de fabricar um identificador solto,
que violaria a chave estrangeira do claim — o defeito corrigido em `e614857`.

## Piloto 1A — execução real

```bash
./poseidon agent start --project 01KY36JQ8A48Q2TVYJMA5Q1N4F \
  --task 01KY36KYHVWB0ZYWCZQPBQZ1ZP \
  --role frontend-specialist --account worker-codex-frontend \
  --instruction "<integração C2/C3>"
```

- `runId` `01KY36MZN4NS77P0FWVGRFZ37C`; branch `task/agent-run-01ky36mzmv5yfdhw9ewfzmk0cm`;
  worktree em `~/Documents/worktrees/<attempt>`;
- claims **exatamente** `docs/frontend/**` e `frontend/**`; fencing `1` na tentativa e `1`
  na conta; sessão Codex real `019f866a-857b-7520-8421-a91a92b275ee`;
- duração ~9 min, `status=completed`.

**Resultado do worker:** 23 arquivos alterados, **zero fora do escapo autorizado**
(verificado por `git status --porcelain` filtrado). Integrou o 202 `state=blocked` com
`blockers`/`nextActions`, publicou os schemas dos eventos canônicos v1.2 no catálogo do
frontend e alinhou os testes de drift.

**Gate do frontend executado na worktree:** `Test Files 1 failed | 56 passed`,
`Tests 1 failed | 499 passed`. As **três** falhas de drift que existiam antes ficaram
verdes; o worker introduziu **uma regressão** em
`frontend/src/features/orchestrator/__tests__/orchestrator.test.tsx`.

O commit do worker (`6e4f713`, identidade `worker-codex-frontend`) ficou na branch da
tentativa e **não foi publicado em `develop`** — o fluxo exige verdict do critic antes.

## CA-7 — critic independente

Superfície: `POST /api/v1/agent-runs/{attemptId}/review` e
`./poseidon agent review <attemptId> --critic <alias> --actor <alias> --diff-file <arquivo>`.

- o critic **não escreve**: roda com `Access=ReadOnly` (Claude: `--tools Read,Grep,Glob`;
  Codex: `--sandbox read-only`), **sem claim de path e sem worktree própria**;
- exige conta **diferente** da do actor — mesma conta é recusada com
  `critic.same_account_as_actor` antes de qualquer execução;
- adquire concessão do próprio perfil com fencing, e libera no `finally`;
- **Default-FAIL**: saída ausente, não-JSON, veredito ausente ou fora do conjunto fechado
  resultam em `fail`. Um `pass` acompanhado de achado P0/P1 é recusado como
  `critic.pass_contradicted_by_findings` — o achado prevalece sobre a aprovação.

### Verdict do Piloto 1A

`reviewId` `01KY37PQX9921N4SHPE620CJ0Q` — critic `chief-claude-primary` (claude-code),
actor `worker-codex-frontend` (codex). **Verdict: `fail`** (`critic.fail`), 9 achados:

| Severidade | Código | Essência |
|---|---|---|
| P0 | `suite-vermelha` | 1 teste falhando; critério "suíte verde" não atendido |
| P1 | `migracao-enum-turno-incompleta` | troca de `chiefTurnState` não fechada |
| P1 | `estado-inicial-desonesto` | default `completed` faz a UI exibir "Concluído" com variante de sucesso **antes de qualquer turno existir** |
| P1 | `codigos-crus-na-ui` | `blocker.code`/`action.code` renderizados como texto, sem i18n |
| P2 | `readiness-do-handle-nao-consumido` | `readiness` exigido no schema mas não consumido |
| P2 | `e2e-sem-evidencia` | 4 specs Playwright alterados sem prova de execução |
| P2 | `catalogo-canonico-nao-verificavel` | SHA-256 citado não verificável pela evidência |
| P2 | `banner-bloqueado-persistente` | aviso de bloqueio não é limpo entre conversas |
| P3 | `disabled-hardcoded` | `disabled={false}` ruidoso |

O achado `estado-inicial-desonesto` é o mais relevante para este produto: o worker fez a UI
**inventar sucesso** sem turno algum — exatamente a classe de defeito que reprovou a RC3. O
critic independente pegou.

## Escolha do critic — fallback declarado

O critic preferencial é `worker-antigravity-review`, que **não foi usado**: o adapter
Antigravity ainda não existe (CA-8) e o perfil não está autenticado. O fallback documentado
`worker-codex-critic` também não está autenticado. Usou-se `chief-claude-primary`, que
satisfaz a independência exigida — conta, executor e concessão distintos do actor, e não
aprova trabalho próprio. **Antigravity permanece first-class e preferencial**; o motivo de
não ter sido usado é ausência de adapter e de autenticação, nunca classificação como
experimental.

Verificado por execução: `agy` com `HOME` isolado exige login OAuth próprio — o isolamento
de conta funciona para Antigravity como funciona para Claude e Codex.

## Cleanup e higiene

Worktree removida, branch da tentativa apagada (o repositório mantém apenas `main` e
`develop`), concessões de conta e claim liberadas, nenhum processo órfão. A saída do worker
foi arquivada como patch **fora do repositório**, em `~/.harness/pilots/`, já que o verdict
foi `fail` e o trabalho não entra em `develop`.

## Gates

Build Release 0 avisos/0 erros; `dotnet format --verify-no-changes` limpo; suíte backend
**415/415**; OpenAPI canônico regenerado; governança sync/generate/lint verde; scan de
segredos limpo. Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi alterado por este
commit — a única alteração nesses caminhos veio do worker governado e permanece fora de
`develop`.

## Go/No-Go do Chief: **No-Go**

Comprovado: turno real, mudança real produzida pelo Codex governado dentro do escopo,
critic independente com verdict auditável, claims e concessões liberadas, zero segredo,
zero órfão.

Não comprovado: **suíte do frontend verde** (1 regressão) e portanto **commit publicado em
`develop`** — corretamente bloqueado pelo verdict `fail`. Restart/recovery controlado
também não foi exercido nesta rodada.

## Próximo passo exato

1. Novo run governado do `worker-codex-frontend` partindo da branch da tentativa (exige
   expor `baseReference` no bootstrap) para fechar os achados P0/P1 — em especial remover o
   estado inicial `completed` e traduzir os códigos de contrato.
2. Re-executar o critic; com `pass`, publicar em `develop` e então exercer restart/recovery.
3. Implementar o adapter Antigravity e autenticar `worker-antigravity-review` para que o
   critic preferencial passe a ser usado.
