# Handoff da Operação Final — leia isto primeiro

Escrito em 2026-08-02 pela instância Integradora que executou a primeira rodada, para
quem vai continuar. Não é resumo de cortesia: é o que eu gostaria de ter recebido.

## Ordem de leitura

1. **`HANDOFF.md`** (este) — situação, armadilhas, o que fazer primeiro.
2. **`STATE.json`** — estado factual. Reconcilie com a realidade antes de confiar.
3. **`FINDINGS.jsonl`** — 26 defeitos, um por linha, com causa, evidência e próximo passo.
4. **`OPERATION-SPEC.md`** — a ordem consolidada e as regras operacionais.
5. **`ORDEM-ORIGINAL.md`** — o texto do proprietário, para conferir minha consolidação.
6. **`ANALISE-COMPORTAMENTAL.md`** — por que o supervisor existe (R1 e R2).

`EVENTS.jsonl` é append-only do supervisor. `METRICS.json` está deliberadamente vazio.

## A regra que governa você

Sua saída é sempre **YIELD**, nunca DONE. Quem decide se acabou é código:

```sh
dotnet run --project src/Harness.OperationSupervisor -- status   # 0 = PASS, 1 = FAIL
```

Os contadores de bloqueio e de trabalho executável são **derivados** de `FINDINGS.jsonl`,
não campos editáveis do `STATE.json` — de propósito: um número que o próprio agente
escreve é um número que ele pode zerar para poder ir embora. Se quiser fechar o gate,
feche defeito.

Para rodar sem ninguém digitando "continue":

```sh
export POSEIDON_INTEGRATOR_COMMAND="claude -p --permission-mode bypassPermissions"
dotnet run --project src/Harness.OperationSupervisor -- run
```

Validado com sessão real. O supervisor entrega `BOOTSTRAP-PROMPT.md` pela entrada padrão.

## Onde a operação está

| eixo | situação |
|---|---|
| defeitos | 18 corrigidos de 26 (**69%**), 8 abertos, 1 bloqueante |
| prova limpa 1–9 | **fase 3 de 9** (projeto `01KZ24JCFRHN2RGP8NHGP75JMK`) |
| produto gerado | **nunca iniciado nem testado** |
| testes de recuperação | parciais — crashes reais recuperados, cenários do §51 não provocados sistematicamente |
| QA do Poseidon | parcial — só humanização, read-only |
| métricas §31/§32 | **não instrumentadas** |
| gate | **FAIL** |

**69% dos defeitos não é 69% da operação.** A parte de correção do núcleo avançou muito; a
parte de **prova** — que a ordem diz valer mais que tudo — mal começou. Estimativa honesta
da operação inteira: **cerca de um terço**.

Os IDs `OPS-NNN` não são uma sequência planejada nem uma barra de progresso: são um
registro de descoberta. `OPS-021` a `OPS-026` nasceram nas últimas horas, achados enquanto
eu corrigia outra coisa. **Espere o total crescer.**

## O que a primeira rodada estabeleceu

Vinte e três commits em `develop`, de `a25bb1ff` até `4c2eb770`. **Nada empurrado para
`origin`.** 1550 unitários verdes, gates do frontend verdes.

A maioria dos defeitos pertence a **uma única família**: *uma falha isolada derrubava o
ciclo inteiro do projeto*. Encontrei três vezes seguidas (validador, conflito de
idempotência, acoplamento entre reconciliação e portões) porque o `try` do
`ChiefBacklogLoopService` envolvia o ciclo todo. Hoje a falha é isolada por card.

A segunda família é **misattribuição de culpa**: falha de infraestrutura contando como
falha do card. Reinício do Host, cancelamento, cota esgotada — cada um matava cards
saudáveis abrindo o circuito, e só o replanejamento da Bruna reabre. Corrigi os três.
**Se aparecer um quarto motivo de infraestrutura, ele provavelmente também conta
indevidamente**: o lugar é `CardCircuitBreakerService.IsInfrastructureFailure`.

A terceira é **oldest-N**: um `LIMIT` sem `DESC` devolvendo os registros mais antigos.
Apareceu três vezes — histórico de mensagens (já corrigido antes), notas de contexto, e
tentativas do quadro. **Se encontrar outro `ORDER BY ... LIMIT` sem `DESC` num caminho de
decisão, desconfie.**

## O defeito bloqueante — `OPS-024`

O laço HITL está aberto: **a decisão do proprietário não vira transição de estado.**

Provei ao vivo, personificando o dono. A Bruna escalou um card, respondi reduzindo o
escopo, ela registrou a decisão com fidelidade — e afirmou *"essa parte volta a andar"*
enquanto o card seguia `escalated`. **Ela relatou progresso que não houve.** Isso é pior
que travar, porque quem está longe do computador acredita nela, e contradiz a honestidade
que ela demonstra no resto da conversa (chegou a dizer, corretamente, *"não vou dizer que
alguém já está codificando quando não está"*).

Três camadas. Fechei duas:

1. **Contrato e handler** (`abd34c6a`) — `cardActions` com conjunto fechado (`replan`),
   `cardId` validado como ULID, checagem de que o card pertence ao projeto do turno, chave
   de idempotência versionada, 5 testes.
2. **Contexto** (`4c2eb770`) — o `ChiefContextComposer` não incluía **card nenhum**. Ela
   literalmente não conhecia o `cardId`. Agora recebe os escalados com id, título e motivo.
3. **ABERTA** — ela recebe o contexto e **ainda assim não emite a ação**. Turno
   `01KZ2BC16PX5TNDCXPCVA1DRJ3` concluiu sem erro, resposta correta em linguagem de
   negócio, zero linhas `decisão do dono` no log, card `01KZ26X4RN6V96W19ZXTKKGTJE` ainda
   `escalated`.

**Próximo passo:** instrumentar os dois lados — quantos cards escalados entraram no
contexto, e se o output trouxe `cardActions`. Se o contexto chega e ela não emite, o
problema é de prompt: dar exemplo concreto de saída na intenção `decidir_escalacao`.
Depois repetir a prova na conversa `01KZ24JKCCWCH0G8C7PJN3AKH4`.

## Armadilhas que vão te custar horas se você não souber

**O `stop` do launcher.** Corrigido (`61ba78e7`), mas saiba: o shutdown gracioso realmente
passa de 20 s. Antes disso, `stop; start` deixava o sistema **fora do ar** exibindo
"Poseidon já está em execução". Se voltar a acontecer, é aí.

**Reiniciar o Host mata trabalho em voo.** Cada reinício meu escalou cards por
cancelamento, até eu corrigir. Reinicie o mínimo necessário e prefira `probe.sh` a
reiniciar para ver o que houve.

**Vocabulário proibido no chat.** `log`, `card`, `arquitetura`, `token`, `backlog`, `ready`
e dezenas de outros estão em `ChiefCommunicationPolicy.TechnicalVocabularyPattern`. Se
você escrever "aplica no card", a resposta da Bruna é **recusada** e o usuário vê uma
falha genérica. A política está certa em proteger a persona; fale como um leigo falaria.

**A imagem de sandbox.** `harness-sandbox-agent:latest` não existia nesta máquina. Sem ela
os anexos não são lidos e os agentes rodam sem isolamento — **e nada avisa**
(`OPS-021`). Construa com `./tools/backend/build-sandbox-image.sh`.

**A conta GLM.** Estava com a cota de 5 horas estourada (reset ~2026-08-03 06:00). O
sistema agora classifica isso corretamente, mas confira `~/.harness/account-availability.json`
antes de culpar o código por runs que morrem sem produzir token.

**Máquina.** 16 GB / 10 cores. `HEAVY = 1` (build completo, suíte completa, Docker build,
Playwright). Agentes LIGHT podem 3–4 em paralelo. **Não leia RAM livre** — leia
`memory_pressure`, swap e pageouts (`./tools/operation/watchdog.sh` já faz isso).

## Ferramentas que deixei prontas

| ferramenta | para quê |
|---|---|
| `tools/operation/watchdog.sh` | estado de todas as tentativas por sinais reais; pressão do host |
| `tools/operation/probe.sh <turn\|card\|attempt> <id>` | acompanha **um sujeito**, devolve controle em ≤30 s (exit 0 concluído, 10 em voo, 20 STALLED) |
| `tools/operation/notify.sh "texto"` | Telegram; descobre o chat sozinho — depende de `OPS-018` |
| `src/Harness.OperationSupervisor` | `status`, `gate`, `run` |
| `src/Modules/Harness.Modules.Operations` | `CompletionGate`, `WaitClassifier`, `HostResourcePolicy` — puros e testados |

## O que eu faria, nesta ordem

1. **Fechar `OPS-024`** — é o único bloqueante e ataca a afirmação central do produto.
2. **`OPS-025`** — escalação sem motivo. Enquanto o review não registra achado
   estruturado, o dono é chamado a decidir às cegas. Anda junto com o 024.
3. **`OPS-021` e `OPS-023`** — sandbox. Hoje os agentes rodam sem isolamento e o sistema
   não reclama; isso também toca o eixo `tool/sandbox enforcement` do §17.
4. **Levar a prova limpa da fase 3 à 9** — é o gate central e o que mais falta.
5. **Iniciar e testar o produto gerado** (§49) — nunca foi feito.
6. **Provocar recovery sistematicamente** (§51) e **QA do Poseidon** (§52).
7. **`OPS-015`** — decidir: ligar o `ToolCallBroker` ao caminho de execução ou registrar
   ADR aceitando o limite. Hoje a regra do `governance/core.md:42-44` não tem
   correspondente estrutural.
8. Os baixos (`OPS-017`, `OPS-020`) e as métricas do §31/§32.

## Coisas que eu errei, para você não repetir

- **Parei três vezes** com trabalho conhecido em aberto. Foi o que motivou o supervisor.
- **Bloqueei nove minutos** num laço contando mensagens enquanto a resposta já existia
  havia 24 segundos. Use `probe.sh`.
- **Introduzi uma regressão** ao tornar o motivo de falha visível: reinícios do Host
  passaram a abrir circuito de cards saudáveis (`OPS-006`, corrigido).
- **Consolidei os prompts em vez de arquivá-los** — só percebi quando o dono perguntou.
  Por isso `ORDEM-ORIGINAL.md` e `ANALISE-COMPORTAMENTAL.md` existem agora.
- **Não instrumentei as métricas** do §31/§32. Deixei `METRICS.json` com `null` em vez de
  número inventado, e recomendo manter essa disciplina.
