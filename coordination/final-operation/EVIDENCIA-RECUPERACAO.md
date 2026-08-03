# Evidência do eixo de recuperação (§31)

Escrito em 2026-08-03 pela Integradora noturna. Cada linha aponta para um fato verificável
no banco (`~/.harness-poseidon/harness.db`) ou numa suíte que roda em segundos. Nada aqui é
"deve funcionar": é o que foi observado.

## 1. Reinício do Host com trabalho em voo — PROVADO AO VIVO

Três reinícios durante a madrugada (03:57, 04:04, 04:08 UTC), todos com tentativas em
execução. Em todos:

- as tentativas em voo terminaram com `operational_state = cancelled`;
- **todos** os cards voltaram para `ready` — nenhum escalado;
- o projeto da prova limpa terminou com **zero** circuitos abertos, mesmo depois de mais de
  cinquenta tentativas fracassadas por causas de infraestrutura.

Consulta: `select count(*) from card_circuit_breakers where project_id='01KZ24JCFRHN2RGP8NHGP75JMK' and state='open'` → `0`.

## 2. Morte do worker — PROVADO AO VIVO

Provocada deliberadamente, num projeto que não é a prova limpa, para não contaminá-la:

```
docker kill harness-sandbox-wsp-ydmx68gpf2e7tpr0p2v1
```

| sujeito | antes | depois |
|---|---|---|
| tentativa `01KZ2WYDMX68GPF2E7TPR0P2V1` | `running` | `rejected` / `cancelled`, `executor.exit_code_137` |
| card `01KYZ9QD7RTVMDKVENWD44XQKN` | `running` | `ready` |
| circuito do card | `closed`, 0 falhas | **`closed`, 0 falhas** |

O que importa é a última linha: o SIGKILL não contou contra o card. Morte de worker é
"nós paramos", não "o enunciado está errado".

## 3. Falha transitória de provedor — PROVADO AO VIVO E FIXADO EM TESTE

A conta codex recusou todos os modelos do plano durante a noite inteira. Depois da correção
do `OPS-034`, a falha passou a ser reconhecida como da CONTA
(`run.account_model_unsupported`), a conta saiu da eleição e **nenhum** card foi penalizado
por isso — os sete cards de Arquitetura seguiram elegíveis e voltaram a executar por outra
conta assim que o papel foi corrigido (`OPS-036`).

Fixado em `TransientProviderFailureRecoveryTests`: seis motivos de infraestrutura, um para
cada vez que um deles custou um circuito aberto nesta operação, mais o contrapeso — falha
depois de trabalho real continua sendo do card.

## 4. Recuperação de tentativa — SUÍTE

`CheckpointResumeTests`, `ProductionDurableExecutionRecoveryTests` (SIGKILL reconciliado em
SQLite e Postgres, sem checkpoint perdido nem duplicado), `IsolatedAttemptRecoveryTests`,
`GovernanceReceiptRecoveryTests` (OCC), `CoordinationLoopGuardRecoveryTests` (circuito e
contadores de laço sobrevivem ao reinício).

```sh
dotnet test tests/Harness.RecoveryTests   # 26 verdes em ~5s
```

## O que este eixo NÃO cobre

- Perda de disco ou corrupção do arquivo do banco: fora do escopo desta operação.
- Recuperação com múltiplos Hosts sobre a mesma raiz: o lock de perfil usa lock em processo,
  não `flock` — está documentado em `AccountProfileProvisioner` e continua sendo limite conhecido.
