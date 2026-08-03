# 11 — Recuperação e resiliência

> Este é, junto com o circuito por card, o eixo mais maduro do produto — e o único com
> evidência de campo em quantidade.

---

## 1. Matriz de recuperação

| Falha | Detector | Intervalo | Transição | Retry | Idempotência | Teste | E2E |
|---|---|---|---|---|---|---|---|
| **Reinício do Host** | boot + reconciliação | boot | tentativa `cancelled` → card `ready` | sim | chave de idempotência na `WorkChain` | ✅ | ✅ **3 reinícios ao vivo, 03/08 03:57/04:04/04:08Z, zero circuito aberto** |
| **Morte do worker** | lease expirado / exit code | 2 min | `rejected`/`cancelled` (`executor.exit_code_137`) → card `ready` | sim | sim | ✅ | ✅ **SIGKILL deliberado, card voltou com circuito `closed`/0 falhas** |
| **Morte da CLI** | exit code + `ExternalFailureKind` | imediato | classificado | conforme tipo | sim | ✅ | ✅ |
| **Tentativa órfã** | `AttemptRecoveryBackgroundService` | **2 min** | lease expirado → requeue | sim | sim | ✅ | ✅ |
| **Card órfão** | `BoardStateReconciliationBackgroundService` | contínuo | reconcilia estado do quadro | — | sim | ✅ | ✅ |
| **Worktree órfã** | `TryHarvestWorktreeAsync` | na colheita | comita sobras, remove worktree, **preserva a branch** | — | sim | ✅ | ✅ |
| **Conta sem cota** | adaptador → `ExternalFailureKind.QuotaExhausted` | imediato | `QuotaLimited` + cooldown até o instante declarado pelo provedor | reeleição | sim | ✅ | ✅ **13:18Z de 03/08: `executor.quota_exhausted` com o instante exato lido do texto do provedor; despacho seguinte foi para outra conta** |
| **Recuperação de conta** | `AccountRecoveryBackgroundService` | **1 min** | `QuotaLimited` → `Available` | — | sim | ✅ | ✅ |
| **Plano não materializado** | `PlanMaterializationReconciliationBackgroundService` | contínuo | retoma o compromisso | sim | **claim por dono vivo** | ✅ | ✅ |
| **Merge não reconciliado** | `MergeReconciliationBackgroundService` | contínuo | reconcilia `merge_intents` | sim | sim | ✅ | ❌ (nunca houve merge de código) |
| **Outbox travada** | `OutboxDispatcherBackgroundService` | configurável | redispatch, `outbox_dispatch_failures` p/ poison | sim | sim | ✅ | ✅ |
| **Execução durável travada** | `DurableExecutionWatchdogBackgroundService` | `PollInterval` | checkpoint + transição | sim | sim | ✅ | ✅ |
| **Ledger divergente** | `LedgerReconciliationBackgroundService` | contínuo | reconcilia | — | sim | ✅ | ✅ |
| **Convergência de estado do projeto/workflow** | 2 hosted services | boot + periódico | converge | — | sim | ✅ | ✅ |

Suíte dedicada: `tests/Harness.RecoveryTests` — 8 arquivos, **28 provas**, todas verdes.
Cobrem: retomada por checkpoint, guarda de laço de coordenação, receipt de governança,
tentativa isolada, execução durável em produção, falha transitória de provedor, link de canal.

---

## 2. O que a evidência de campo mostra

Fonte: `coordination/final-operation/EVIDENCIA-RECUPERACAO.md`, com ids e consultas.

- **3 reinícios do Host com trabalho em voo**: todas as tentativas terminaram `cancelled`, todos
  os cards voltaram a `ready`, **nenhum escalado**;
- `select count(*) from card_circuit_breakers where project_id='01KZ24JCFRHN2RGP8NHGP75JMK'
  and state='open'` → **0**, depois de mais de **50 tentativas fracassadas por infraestrutura**;
- morte de worker por `docker kill`: card voltou com circuito `closed`, 0 falhas.

> **A distinção que o produto vende — "falhar por cota é diferente de falhar por trabalho ruim" —
> está PROVADA em campo.** Este é o achado mais favorável da auditoria inteira.

---

## 3. O Operation Supervisor (§28)

`src/Harness.OperationSupervisor/Program.cs`.

| Pergunta | Resposta |
|---|---|
| Monitora o Poseidon? | **Não.** Monitora a **Integradora** (a instância de agente que conduz a operação de validação) |
| CompletionGate? | **Sim** — `Harness.Modules.Operations.CompletionGate` é quem decide se a operação acabou |
| Relaunch? | **Sim** — se a Integradora encerrar com o gate em FAIL, o supervisor a relança sozinho |
| Heartbeat? | Indireto, por monitoramento de processo |
| Resource coordinator? | **Não** |
| Quota? | **Não** |
| Checkpoint? | Via `STATE.json` / `LEASE.json` em `coordination/final-operation/` |

**O problema que ele resolve, nas palavras do próprio código:** *"instrução não é mecanismo. Uma
instância Integradora pode, com probabilidade não desprezível, decidir que chegou a um bom ponto
de fechamento e emitir um relatório final com trabalho conhecido em aberto. Aumentar o prompt
reduz a chance; não a elimina. Aqui a continuidade deixa de depender do juízo do modelo: a saída
dele é sempre YIELD, e quem decide se a operação acabou é o CompletionGate."*

Verbos: `gate` (exit 0/1, serve para CI), `status`, `run` (laço de supervisão).

### Implementado vs. realmente ativo nesta instalação

| Mecanismo | Implementado | Ativo agora |
|---|---|---|
| `CompletionGate` | ✅ | ✅ (`supervisor metrics` alimentou `METRICS.json`) |
| Laço `run` com relaunch | ✅ | ⚠️ **intermitente** — `supervisor.log` e `supervisor-swap.log` existem; o `STATE.json` registra que um vigia armado por uma sessão **morreu junto com ela** e ninguém publicou o binário |
| Watchdog de binário defasado | ✅ (`tools/operation/watchdog.sh`) | script externo, não produto |
| Sonda de tentativa (`probe.sh`) | ✅ | script externo, não produto |

**Achado `F-25` — MEDIUM, ARCHITECTURAL RISK.** A supervisão da operação vive **fora do produto**,
em scripts shell (`tools/operation/*.sh`) e num binário separado, cujo ciclo de vida está atado à
sessão de agente que o iniciou. Isso funcionou como andaime de validação; não é mecanismo de
produto. Um cliente não vai rodar `supervisor-start.sh`.

---

## 4. O que a resiliência NÃO cobre

| Lacuna | Impacto |
|---|---|
| Estado de backoff em memória (`_reviewBackoff`, `_noProgressRuns`, `_dispatchBackoff`, `_reviewInfrastructureFailures`) | Reiniciar o Host **zera contadores de adiamento**. Ajuda a destravar, mas apaga o histórico que justificaria escalar — `F-24` |
| Sem coordenador de recurso pesado | 7 agentes podem disparar 7 builds — `F-20` |
| Sem watchdog de binário **dentro do produto** | O Host pode rodar código mais velho que o conserto por horas (custou 310 linhas de log errado em 03/08) |
| Recuperação de **cota da Chief** | Não existe: o seletor da Chief não consulta cota — `F-01` |
| Recuperação de **card escalado por falta de revisor** | Bloqueada até o conserto no working tree ser commitado — `F-12` |
