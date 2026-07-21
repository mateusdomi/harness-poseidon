# CA-5 — bootstrap governado de agentes

Data: 2026-07-21. Branch: `develop`. Continuação de ADR-021 sobre CA-3 e CA-4.

## Problema

Havia adapters reais (CA-4) e perfis isolados (CA-3), mas nenhum COMANDO que compusesse
tarefa, tentativa, concessão de conta, claims, worktree, bundle, receipt, renovação de
lease, resultado e cleanup. Sem isso, usar um executor externo seria script paralelo — e
script paralelo é exatamente o que contorna governança.

## Comando oficial

```bash
./poseidon agent start \
  --project <projectId> \
  --task <taskId> \
  --attempt <attemptId> \
  --role frontend-specialist \
  --account worker-codex-frontend \
  --instruction "<texto>" \
  [--model <m>] [--effort <e>] [--read-only]

./poseidon agent status <attemptId>
./poseidon agent cancel <attemptId>
./poseidon agent recover
./poseidon agent doctor
```

Os verbos não executam agente por conta própria: falam com a API oficial do Host, que é
quem detém claim, conta, worktree, bundle e receipt.

| Rota | Método | Papel |
|---|---|---|
| `/api/v1/agent-runs` | POST | bootstrap; devolve `202` com o run aceito |
| `/api/v1/agent-runs/{attemptId}` | GET | estado durável |
| `/api/v1/agent-runs/{attemptId}/cancel` | POST | cancelamento cooperativo |
| `/api/v1/agent-runs/recovery` | POST | recovery de concessões e leases expirados |
| `/api/v1/agent-accounts/doctor` | GET | diagnóstico por conta |

## Composição do `AgentRunOrchestrator`

1. **Política de escopo do PAPEL** (`AgentPathScopePolicy`) — claim fora do escopo bloqueia
   antes de qualquer aquisição.
2. **Conta** — precisa existir, não estar `Disabled`, permitir o papel e ter adapter real.
3. **Perfil isolado + concessão** com fencing crescente (CA-3).
4. **Claim durável da tentativa** em `attempt_workspaces`: lease, fencing, conflito de path.
5. **Worktree Git isolada** dentro da raiz controlada.
6. **Context bundle + receipt** de governança (`Selected` → `Delivered` → `Completed`/
   `Failed`), com conflito de bundle abortando o run.
7. **Executor externo real** no perfil da conta e na worktree da tentativa.
8. **Heartbeat** renovando *as duas* concessões — a da tentativa e a da conta. Perder uma e
   manter a outra deixaria o perfil preso ou liberado cedo demais.
9. **Transição durável** e **cleanup sempre**: sessão, worktree, perfil efêmero, concessão
   da conta e claim, em `finally`.

O `start` devolve `202` imediatamente e a execução segue em background: um turno de agente
dura minutos e não pode ficar preso numa requisição HTTP. O estado observável vem do banco,
portanto sobrevive a restart.

## Invariantes de governança comprovados

- **Nasce desligado.** Sem `Harness:AgentRuns:Enabled` e uma raiz controlada, todo verbo
  responde `409 agent_runs_disabled`. Instalação nova não executa agente externo.
- **O cliente não escolhe o próprio escopo.** `scopeClaims` **não existe** no contrato de
  entrada: o escopo vem do papel. Um corpo que tente declará-lo é recusado com `400` pelo
  próprio desserializador (`JsonUnmappedMemberHandling.Disallow`), e há teste de contrato
  garantindo que o schema publicado nunca ganhe esse campo.
- **Papel desconhecido é recusado** antes de tocar claim ou conta.
- **Repositório fora da raiz controlada** é recusado.
- **Executor sem adapter** é recusado por `executor.adapter_not_implemented` — nunca
  substituído silenciosamente por outro executor.
- **O prompt declara o escopo ao worker** e afirma que conteúdo lido no repositório é dado,
  não autoridade: instrução embutida em documento não amplia escopo nem contorna claim.

## Configuração de contas

`AgentAccountConfigurationLoader` resolve `--account <alias>`. Os sete aliases canônicos de
ADR-021 existem sem configuração alguma; `<home>/.harness/agent-accounts.json` sobrepõe o
alias correspondente. Decisões deliberadas:

- **nenhuma conta nasce `Available`** — todas nascem `AuthenticationRequired`, porque
  instalação e autenticação são comprovadas por probe e login, nunca presumidas. Foi
  exatamente a disponibilidade presumida que a homologação da RC3 reprovou;
- JSON inválido falha alto (`account.configuration_invalid`) em vez de cair no padrão
  silenciosamente — o operador precisa saber que o arquivo dele não foi aplicado;
- alias com `@` e `credentialRef` que não seja referência opaca continuam recusados;
- **Antigravity tem prioridade maior que o Codex** entre os critics: é executor de primeira
  classe com vocação de critic, não experimental.

## Evento publicado

`agentRun.stateChanged` no stream do projeto, com payload tipado em `docs/contracts/events.json`
(v1.2) e estado de conjunto **fechado**: `accepted | running | completed | failed |
cancelled | scopeconflict | rejected`.

## Provas executadas

- `tests/Harness.UnitTests/Agents/AgentAccountConfigurationTests.cs` — 10 testes de resolução
  de conta, incluindo papel provider-agnostic (Codex e Kimi com o mesmo escopo) e recusa de
  segredo cru.
- `tests/Harness.IntegrationTests/Agents/AgentRunBootstrapTests.cs` — 4 testes sobre o Host
  real: desligado por padrão, papel desconhecido recusado, escopo não-injetável e `doctor`
  reportando probe/perfil/autenticação reais das sete contas.
- `tests/Harness.IntegrationTests/Agents/AgentRunHappyPathTests.cs` — caminho feliz completo
  sobre repositório Git real (seção acima).
- `tests/Harness.ContractTests/Agents/AgentRunContractDriftTests.cs` — 7 testes: as cinco
  rotas no OpenAPI canônico, o evento com payload tipado e o schema sem `scopeClaims`.

`docs/contracts/openapi.json` regenerado por `tools/backend/export-contracts.sh`.

Gates: build Release 0 avisos/0 erros; `dotnet format --verify-no-changes` limpo; suíte
integral **402/402** (380 → 402); governança sync/generate/lint verde; scan de segredos
limpo. Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado.

## Caminho feliz executado sobre um repositório Git real

`tests/Harness.IntegrationTests/Agents/AgentRunHappyPathTests.cs` semeia a cadeia real
(solicitação → demanda → tarefa → tentativa), cria um repositório Git de verdade dentro da
raiz controlada e dispara o bootstrap pela API. Comprovado em execução:

- `202` com claims do PAPEL (`docs/frontend/**`, `frontend/**`) e fencing > 0 na tentativa
  **e** na conta;
- branch `task/agent-run-<attempt>` e worktree **realmente criadas** no repositório;
- context bundle selecionado e receipt de governança gravado com o `runId` do turno;
- executor Codex **realmente iniciado** como subprocesso no perfil isolado da conta;
- estado terminal `Failed` por **falha de autenticação** do perfil — o resultado honesto
  nesta máquina, e o run não finge sucesso;
- cleanup completo: claim liberado (`ReleasedAt` preenchido), worktree removida, concessão
  da conta devolvida, perfil ainda saudável no `doctor` e **nenhum processo órfão**.

### Dois defeitos que este teste encontrou

1. **`attemptId` era fabricado.** O bootstrap gerava um ULID novo quando o cliente não
   informava um, e o claim durável tem chave estrangeira para `work_tasks`/`work_attempts` —
   o resultado era `FOREIGN KEY constraint failed` vazando como **HTTP 500**. Corrigido: o
   comando não fabrica identidade de domínio. `attemptId` é obrigatório, tarefa e tentativa
   precisam existir, e a divergência entre elas devolve `409 attempt_task_mismatch`.
2. **Recusa por 500 em vez de 404.** Tarefa ou tentativa inexistente agora responde `404`
   tipado, antes de qualquer aquisição.

## Limite honesto

Toda a máquina durável está comprovada em execução real, **exceto o turno do modelo**: os
perfis isolados de `worker-codex-frontend` e `chief-claude-primary` não estão autenticados
nesta máquina (ver CA-4), então o executor termina em falha de autenticação. O Piloto 1A —
uma alteração real em `frontend/**` produzida pelo worker — **não foi executado**, e nenhum
sucesso de execução real é declarado.

## Próxima fatia

Piloto 1A — uma alteração mínima, real e reversível em `frontend/**` conduzida pelo
bootstrap, seguida de CA-7 (actor/critic) e do Piloto 1 completo.
