# Handoff da Operação Final — leia isto primeiro

Escrito em 2026-08-02 pela instância Integradora que executou a primeira rodada, para
quem vai continuar. Não é resumo de cortesia: é o que eu gostaria de ter recebido.

## Ordem de leitura

1. **`HANDOFF.md`** (este) — situação, armadilhas, o que fazer primeiro.
2. **`STATE.json`** — estado factual. Reconcilie com a realidade antes de confiar.
3. **`FINDINGS.jsonl`** — os defeitos, um por linha, com causa, evidência e próximo passo.
4. **`OPERATION-SPEC.md`** — a ordem consolidada e as regras operacionais.
5. **`ORDEM-ORIGINAL.md`** — o texto do proprietário, para conferir minha consolidação.
6. **`ANALISE-COMPORTAMENTAL.md`** — por que o supervisor existe (R1 e R2).

`EVENTS.jsonl` é append-only do supervisor. `METRICS.json` tem números medidos — regenere
com o verbo `metrics` antes de citá-los.

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
tools/operation/supervisor-start.sh [PID-da-sessao-atual]
```

O script publica o binário antes (não recompila às 3 da manhã), desacopla com `nohup` e
recusa subir em duplicidade. Passando o PID da sessão que está trabalhando, o supervisor
**observa** em vez de duplicar e só lança a sucessora quando aquele processo morrer — então
ele pode subir AGORA, no meio da sessão, e assumir a madrugada sozinho quando ela cair.

Estado do supervisor em um olhar: `cat coordination/final-operation/LEASE.json` (pid,
heartbeat, ciclo) e `coordination/final-operation/supervisor.log`.

**Ele também toma a vaga de uma Integradora que parou.** Processo vivo não prova trabalho —
uma sessão que encerrou o raciocínio mas cuja janela continua aberta seguraria a vaga para
sempre. O sinal usado é o repositório andando: sem commit por 45 minutos
(`POSEIDON_INTEGRATOR_IDLE_MINUTES`), a vaga é tratada como devolvida e a sucessora sobe.
Consequência prática que você precisa saber: **pode existir uma sessão antiga aberta na tela
enquanto você trabalha.** Ela não vai mexer em nada sozinha; se o dono digitar nela, aí sim
existem duas — e quem chegou depois é você.

## Onde a operação está

| eixo | situação |
|---|---|
| defeitos | 33 corrigidos de 37, **4 abertos e TODOS do proprietário** (contas/Telegram) |
| prova limpa 1–9 | **fase 3 de 9** (projeto `01KZ24JCFRHN2RGP8NHGP75JMK`) — destravada, executando |
| produto gerado | **nunca iniciado nem testado** |
| testes de recuperação | **pass** — ver `EVIDENCIA-RECUPERACAO.md` |
| QA do Poseidon | parcial — só humanização, read-only |
| métricas §31/§32 | **medidas** — `METRICS.json` tem números reais |
| gate | **FAIL** |

**75% dos defeitos não é 75% da operação.** A parte de correção do núcleo avançou muito; a
parte de **prova** — que a ordem diz valer mais que tudo — mal começou. Estimativa honesta
da operação inteira: **cerca de um terço**.

Os IDs `OPS-NNN` não são uma sequência planejada nem uma barra de progresso: são um
registro de descoberta. `OPS-021` a `OPS-026` nasceram nas últimas horas, achados enquanto
eu corrigia outra coisa. **Espere o total crescer.**

## O que a primeira rodada estabeleceu

Mais de trinta commits em `develop` a partir de `a25bb1ff`, por duas instâncias em
paralelo. **Nada empurrado para `origin`.** ~1580 unitários verdes, gates do frontend
verdes.

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

## O caso que mais ensinou — `OPS-024`, já fechado

Vale ler porque o padrão vai se repetir. A Bruna escalou um card, o dono respondeu
reduzindo o escopo, ela registrou a decisão com fidelidade — e afirmou *"essa parte volta
a andar"* enquanto o card seguia `escalated`. **Relatou progresso que não houve.** Isso é
pior que travar, porque quem está longe acredita nela.

Tinha QUATRO camadas, e cada uma parecia ser a última: faltava a ação no contrato de saída;
faltava o `cardId` no contexto do turno; a reserialização descartava a ação já emitida; e o
replanejamento devolvia o card sem fechar o circuito, que re-escalava no ciclo seguinte.

A lição: quando um efeito não acontece, **verifique cada elo da cadeia até o estado
persistido** — não pare no primeiro elo consertado. E desconfie de qualquer afirmação de
progresso que você não tenha conferido no banco.

## O que a medição revelou

`METRICS.json` deixou de ser um arquivo de `null`. Dois números mudam a prioridade:

- **desperdício por falha transitória: 0,98%.** O §34 registrava ~18% no piloto anterior e
  pedia <5%. A meta foi batida e agora é verificável a qualquer momento
  (`dotnet run --project src/Harness.OperationSupervisor -- metrics`).
- **retrabalho: US$ 221 de US$ 303 — 73% do custo.** Esse é o gargalo real e estava
  invisível. Com utilização de modelo em 14,6%, o quadro é que a operação passa a maior
  parte do tempo não produzindo. É o insumo que faltava para a decisão de concorrência do
  §18, que continua sem benchmark.

Duas estatísticas foram corrigidas depois da primeira medição, em vez de publicadas: a
média de duração dizia 84 minutos porque uma única tentativa órfã ficou 71 horas aberta —
a mediana real é 5,8 minutos. Prefira sempre a mediana aqui.

## Trabalhar em paralelo com outro agente

Testado nesta operação com o Kimi no mesmo repositório. O que funciona:

1. **Worktree separado** (`git worktree add`), nunca editar no working tree do outro.
2. **Antes de escolher um item**, comparar os arquivos que ele tem em voo (`git status`)
   com os que o item exige. Duas das minhas escolhas iniciais colidiam e foram trocadas.
3. **Integrar com `git merge --ff-only`** só quando não houver sobreposição — o merge
   preserva o trabalho não commitado do outro e falha em vez de sobrescrever.
4. **Nunca editar `FINDINGS.jsonl` fora do momento do commit**: os dois lados escrevem
   nele, e é o arquivo que alimenta o gate. Se ele estiver em voo do outro lado, adie.
5. **Não reiniciar o Host nem escrever no chat da Bruna** — isso mata o teste alheio.

Consequência prática: uma correção pode ficar **retida**. É melhor que corromper o commit
do outro.

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

**O diretório de sessão do executor.** Regressão observada em 2026-08-03: os cards da
Fase 3 morriam com `executor.turn_failed: Failed to write last message file
".harness/accounts/<conta>/sessions/last-message-<id>.txt": No such file or directory`. O
perfil isolado não cria (ou não deixa gravável) o `sessions/` no caminho novo de execução
em contêiner. O CLI degrada para "mensagem vazia" e o run morre sem produzir token.

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

---

# Noite de 2026-08-03 — o que mudou e o que aprendi

## O defeito que teria custado a noite inteira

O supervisor saía no PRIMEIRO ciclo com "Bloqueio humano" sempre que existisse **qualquer**
bloqueador externo registrado. Havia três (contas sem credencial) — e quatro defeitos que a
Integradora fazia sozinha, mais a prova limpa parada na fase 3. A supervisão inteira estava
desligada por uma pergunta mal formulada.

Agora o dono da ação é **declarado** no finding (`"owner": "human"`), não adivinhado no
texto, e só se chama o proprietário quando NADA que dispensa o proprietário restou: zero
findings do agente **e** os três eixos de prova verdes. Se você registrar um finding novo,
ele nasce do agente por omissão — o default importa: se nascesse "do humano", a operação
pararia sozinha ao registrá-lo.

## A família de defeitos desta noite: **a causa apagada pelo envelope**

Três defeitos independentes, o mesmo formato — algo genérico escrito por cima de algo
específico, e a informação que resolvia o problema desaparecendo em silêncio:

1. `turn.failed` do Codex sobrescrevia `account_model_unsupported`. Falha genérica de turno
   não marca conta indisponível ⇒ a eleição mandou card para uma conta morta por horas.
2. A cauda de diagnóstico de dez linhas perdia a linha que explicava para um aviso tardio.
   O `failure_reason` no banco mostrava um aviso onde deveria mostrar a causa.
3. A sonda chamava de `COMPLETED` um card em `ready` — inclusive um que tinha voltado à fila
   depois de cinquenta tentativas fracassadas.

**Se você encontrar mais um lugar onde um código genérico é escrito sem perguntar se já
existe um específico, desconfie.** O padrão é o mesmo do `oldest-N` do handoff anterior:
aparece três vezes antes de alguém nomear.

## A armadilha que mais enganou: PID vivo não prova trabalho

Uma tentativa ficou **dezesseis minutos** com processo vivo, CPU, memória e contêiner de pé
— sem UMA conexão ao endpoint do modelo. O log do proxy explicava em uma linha repetida
centenas de vezes: `proxy-deny statsig.anthropic.com:443`. A CLI tentava telemetria **antes**
do trabalho, o egresso restrito negava corretamente, e ela reentrava no retry.

De fora era indistinguível de "o agente está pensando".

Quando uma tentativa passar de ~10 minutos sem token, **leia o log do proxy da tentativa**
antes de qualquer outra hipótese:

```sh
docker logs harness-proxy-wsp-<sufixo-do-attempt> | grep -v statsig | sort | uniq -c
```

Zero linhas para o endpoint do modelo com a allowlist certa carregada = a CLI nem chegou lá.

## O bloqueio real da Fase 3 não era o que o finding dizia

O `OPS-032` (renumerado para `OPS-033`) dizia "o perfil isolado não cria o `sessions/`". Não
era: o diretório existia em disco desde antes das falhas. A CLI do Codex roda com **sandbox
própria** (`--sandbox workspace-write`, raízes graváveis = workdir, `/tmp`, `$TMPDIR`) e o
alvo estava fora dela.

E, corrigido isso, o bloqueio seguinte era de ROTEAMENTO: os sete cards de Arquitetura
resolviam para `frontend-specialist` porque o texto dizia "componente" — vocabulário de
arquitetura antes de ser de interface, o C4 tem um nível com esse nome — e depois porque um
SAD de quatro mil caracteres cita "tela" uma vez. Como só uma conta serve esse papel, a fase
inteira morreu junto com aquela conta. **A persona já dizia "Arquiteto" e o papel dizia outra
coisa: a contradição estava visível e ninguém a lia.**

Lição operacional: quando um card não anda, leia a linha `adiado:` inteira. Ela lista TODAS
as contas com o motivo de cada uma — e `role_not_allowed` em todas menos uma é um diagnóstico
de roteamento, não de disponibilidade.

## Madrugada de 2026-08-03, segunda rodada — a operação parou de esbarrar em si mesma

### O diagnóstico que muda a sua noite: não há conta de ator viva

Se você chegou aqui esperando tocar a prova limpa, leia isto antes de tentar: **nenhuma
conta executa papel de ator agora.** Confirmado ao vivo, não deduzido de arquivo:

- `worker-glm-general` — toda chamada volta `429 [1310] Weekly/Monthly Limit Exhausted`.
  A cota é **semanal** e volta em **2026-08-06 10:11**. Reproduzido à mão dentro do
  contêiner às 04:56.
- `chief-claude-primary` e `worker-claude-secondary` — sem credencial dentro do contêiner
  (`OPS-030`, decisão de POLÍTICA do dono).
- `worker-codex-frontend` — recusa todo modelo do CLI instalado (`OPS-029`).

Ou seja: a prova limpa está parada por **falta de insumo**, não por defeito. O projeto
`01KZ24JCFRHN2RGP8NHGP75JMK` está **pausado de propósito** para não queimar card contra
conta morta. Para retomar quando houver conta:

```sh
curl -X POST http://127.0.0.1:5173/api/v1/projects/01KZ24JCFRHN2RGP8NHGP75JMK/chief/resume
```

**Não fique tentando.** Cada tentativa contra conta sem cota gasta dez minutos e não
produz um token.

### A família desta rodada: o sistema não sabia o que ele mesmo já sabia

Três defeitos, o mesmo formato — a informação existia e ninguém a lia:

1. **O provedor DIZ quando a cota volta** e o classificador jogava fora, aplicando sempre
   três horas. Cota semanal tratada como janela de três horas vira laço: a conta reaparece
   elegível, é eleita, o card morre de novo. (`OPS-038`)
2. **A CLI emudece depois do 429** — processo vivo, zero conexões, zero saída, para sempre.
   Só o timeout de trinta minutos a encerrava, e timeout classifica como transitório: tudo
   de novo, na mesma conta morta. Agora existe vigia de SILÊNCIO com código próprio,
   `executor.no_progress`. (`OPS-039`)
3. **O motivo estava escrito, dentro do contêiner.** No modo `-p` o Claude Code 2.0.30 não
   manda UMA linha para stderr: o 429 vai só para `<config home>/debug/<sessão>.txt`.
   Testei `--debug` e `--debug api`: stderr vazio nos dois. Agora, quando o stderr não
   explica, o executor lê a cauda do log da própria CLI — por `docker exec` quando há
   sandbox. (`OPS-040`)

**Se você encontrar mais um lugar onde o sistema decide sem consultar o que já observou,
desconfie.** É a mesma forma do `oldest-N` e do `envelope apaga a causa`.

### Como eu diagnostiquei — vale repetir

Quatro comandos, nesta ordem, e o caso estava fechado em vinte minutos:

```sh
./tools/operation/watchdog.sh                       # 3 STALLED, sem pid, sem chamada
docker exec <sandbox> sh -c 'cat /proc/7/wchan; cat /proc/7/net/tcp'   # epoll, zero conexão
docker exec <sandbox> tail -c 900 /codex-state/debug/<sessão>.txt      # o 429 completo
docker logs harness-proxy-wsp-<sufixo> | grep -v statsig | sort | uniq -c
```

O terceiro é o que faltava no handoff anterior: **quando o proxy está limpo e a CLI está
muda, o log DELA é o único lugar onde o motivo existe.**

### Coisas que eu errei nesta rodada

- **Cancelei a tentativa sem antes tirar a conta morta da eleição.** O despacho seguinte
  saiu em segundos, para a mesma conta, e travou igual. Só então pausei o projeto. Ordem
  certa: primeiro tirar a causa de circulação, depois cancelar.
- **Chutei o ID de uma tentativa** a partir do prefixo que o watchdog mostra. `agent cancel`
  aceitou e não fez nada visível. Leia o ID inteiro do banco.

### O que ficou pronto e o que não

`OPS-018` fechou: `notify.sh` entregou mensagem real — o Telegram funciona fim a fim, o
destino é descoberto sozinho. **Sobraram três defeitos, todos do proprietário**, e todos
sobre conta. `OPS-041` (a pausa não alcança o que está em voo) nasceu e morreu nesta
rodada: o contrato existia, só não era declarado.

`OPS-018` (Telegram), `OPS-028` (cota GLM), `OPS-029` (plano ChatGPT), `OPS-030` (credencial
Claude no Keychain). Nenhum deles é executável daqui — e a decisão do `OPS-030` é de
POLÍTICA, não técnica: extrair OAuth do Keychain para disco viola a regra de nunca persistir
segredo. Não a tome sozinho.

---

# Manhã de 2026-08-03, ciclo 2 do supervisor — o laço que girava em falso

## Leia isto antes de tentar qualquer coisa com conta

Continua valendo o diagnóstico da rodada anterior, e eu confirmei ao vivo no próprio
sistema (`~/.harness/account-availability.json`, não deduzido de texto):

| conta | estado | papel |
|---|---|---|
| `worker-glm-general` | `QuotaLimited` até **2026-08-06 10:11** | backend-specialist |
| `worker-claude-secondary` | `AuthenticationRequired` | backend-specialist |
| `worker-codex-frontend` | `account_model_unsupported` | frontend-specialist |
| `chief-claude-primary` | `AuthenticationRequired` desde 03:17 | **chief-orchestrator** |

A linha que a rodada anterior não tinha registrado é a última: **a conta que dá voz à
Bruna também caiu.** Ela é a única com o papel `chief-orchestrator`. Sem ela não há nem
chat — o que significa que a prova limpa, o produto gerado e o QA do Poseidon estão os
três parados pelo mesmo motivo. Não é "a prova está lenta": o produto não responde.

`worker-codex-critic` e `worker-antigravity-review` aparecem `Available`, mas ambos só
têm o papel `critic`. Nenhum deles executa card de documento — o pacote de objetivo é
emitido com `Capacidade de execução autorizada: backend-specialist` fixo
(`WorkflowPhaseDriver.ComposeObjectiveInstruction`). **Não perca tempo tentando eleger
um crítico como ator.**

## O defeito desta rodada — `OPS-042`, e por que ele custaria três dias

O supervisor exigia os **três eixos de prova VERDES** para admitir bloqueio humano. Mas a
prova limpa só sai de `blocked` com uma conta viva. Logo o eixo nunca ficava verde,
`NeedsHuman` devolvia `false` para sempre, e o supervisor **relançava a Integradora ciclo
após ciclo contra zero trabalho, em silêncio**, enquanto o dono dormia sem saber que a
operação dependia dele.

A correção da madrugada tinha trocado "chamar cedo demais" por "**nunca chamar**". O
primeiro acorda alguém à toa; o segundo desperdiça a noite inteira. Repare no formato:
consertar um erro de julgamento com uma regra mais dura produziu o erro simétrico. Se
você for endurecer uma condição de parada, **pergunte qual caso ela torna impossível.**

Agora o finding **declara** os eixos que bloqueia (`blocksAxes`, default lista VAZIA para
não virar a saída fácil), e `NeedsHuman` exige cada eixo `pass` **ou** declaradamente
segurado pelo dono — mais zero trabalho do agente e nenhum gate/teste obrigatório
pendente. O gate segue FAIL: chamar o dono não conclui nada.

Duas consequências que eu tratei junto, porque parar sem elas seria fingir que resolvi:

- **o supervisor notifica** (`notify.sh`, evento `operation.human_notify`) em vez de parar
  num terminal que ninguém está olhando;
- **ele espera em vez de sair.** Sair transformaria "assim que houver conta eu retomo" em
  promessa falsa: o dono destravaria a conta e nada aconteceria. A espera reavalia a cada
  15 min (`POSEIDON_SUPERVISOR_HUMAN_RECHECK_SECONDS`), com heartbeat, por até 24 h
  (`POSEIDON_SUPERVISOR_HUMAN_WAIT_HOURS`).

**O proprietário foi notificado no Telegram às 06:14 UTC** com as três decisões.

## O motor dos 73% de retrabalho tinha nome — `OPS-043`

As métricas diziam havia dias que retrabalho era 73% do custo e ninguém sabia de onde
vinha. Vinha daqui: **uma decisão do usuário sobre UM artefato criava card de atualização
para TODOS os artefatos da fase.**

No banco da prova limpa, a corrente inteira está visível: a mensagem *"Decisão sobre o
Plano de Observabilidade: reduzir o escopo"* gerou atualização do **Comparativo de
trade-off**; a seguinte, sobre o **SAD**, gerou outra do Comparativo; a sobre o **C4**
gerou uma do **DER**. Cinco gerações do mesmo Comparativo em uma hora, nenhuma delas
citada em mensagem nenhuma.

`IsDocumentRevisionRelevant` só filtrava assunto de agenda/prazo — todo o resto era
"relevante para todos". E a mensagem **nomeava o artefato**. É exatamente a forma do
`oldest-N` e do `envelope apaga a causa`: **a informação existia e o código não a lia.**
Essa é a terceira ocorrência da mesma família nesta operação; ela vai aparecer de novo.

O estreitamento é tímido de propósito: só vale quando a mensagem nomeia **exatamente um**
artefato da fase. Zero ou dois mantêm o conservador, porque perder uma regra de negócio
continua sendo pior que uma revisão a mais.

## O que eu quase errei

Ia matar o supervisor para publicar o binário novo **enquanto ele lia a minha saída
padrão**. Ele é o processo que me lançou: derrubá-lo fecha o cano e mata esta sessão no
meio da escrita. Persisti handoff, estado e commit **antes** da troca. Se você precisar
trocar o supervisor de baixo de si, essa é a ordem — grave primeiro, troque depois.

## Estado ao encerrar

`verify.sh` verde de ponta a ponta (exit 0): 1635 unitários, 306 de integração, 26 de
recuperação, 42 de contrato, 22 de arquitetura, 8 de concorrência, 783 de frontend e os
132 do Playwright. Nada empurrado para `origin`.

Os 7 cards de Arquitetura em `ready` estão listados por ID no `nextAction` do
`STATE.json`. Quando existir UMA conta:

```sh
curl -X POST http://127.0.0.1:5173/api/v1/projects/01KZ24JCFRHN2RGP8NHGP75JMK/chief/resume
```


---

# Sessão de 2026-08-03 (manhã) — o dono voltou, o contêiner saiu

## A decisão do proprietário

O contêiner deixou de ser exigido nesta instalação
(`Harness__IsolatedExecution__Mode=Disabled` + `UncontainedExecutionAcknowledged` no
`~/.harness/poseidon.env`). Motivo medido, não preferência: as credenciais do Claude Code
vivem no Keychain do macOS, que **não existe dentro do contêiner**. A mesma conta, com o
mesmo config home, responde no host e responde `Invalid API key · Please run /login` lá
dentro. Com todas as contas de ator nessa situação, o isolamento não continha risco —
cegava a frota. Reverter é apagar três linhas daquele arquivo.

**A configuração desta instalação NÃO está no `appsettings.json` do repositório.** Está em
`~/.harness/poseidon.env`. Perdi tempo editando o lugar errado.

## O defeito que mentia sobre cota

O GLM é o **mesmo binário** do Claude Code apontado a outro endpoint por ambiente, e o
`poseidon` carrega esse ambiente no processo do Host. Bastava uma variável `ANTHROPIC_*`
sobreviver para uma conta Claude — autenticada, com cota — falar com o endpoint do GLM e
morrer na cota **dele**. Foi isso que produziu "nenhuma conta de ator disponível" e acordou
o dono à toa. Agora quem não declara essas variáveis as recebe **zeradas**: não copiar não
bastava.

## A família desta sessão: **ordem que condena**

Dois entregáveis de Arquitetura ficaram **treze horas** em `ready`. Três camadas somadas:

1. o desempate entre cards de mesma prioridade era a ordem de leitura da rodada — quem
   perde uma rodada volta para o fim e nunca sai;
2. o `EnqueuedAt` existia para resolver isso e não participava do desempate;
3. envelhecer a fila do teto global não bastou, porque **o planejador reordenava depois**.

E o gargalo por trás de tudo: cards da mesma fase reivindicam os **mesmos caminhos**, então
serializam por escopo — um por vez, por design. Se uma fase parecer parada, procure
`escopo ocupado por run vivo` antes de suspeitar de conta.

## Instrumentos que passaram a existir

- `[executor-env]` no log: as CHAVES do ambiente entregue à CLI (nunca os valores). Foi essa
  linha que revelou o `CLAUDE_CONFIG_DIR` faltando.
- `card ... retido pelo teto global`: o corte por capacidade deixou de ser só um contador.
- `card ... NÃO recebeu desfecho`: invariante em Warning — nenhum card sai de um ciclo sem
  despacho, adiamento ou retenção registrados.
- `chief.account_slots_full`: slot cheio deixou de se anunciar como falta de conta com a
  hora de reset de outra conta.

## Um erro meu, para não repetir

Registrei que um card "sumia em silêncio". Não sumia: a mensagem existia com outro texto
(`escopo ocupado por run vivo`) e eu não a procurei. Antes de declarar silêncio, agregue as
linhas do card por TIPO — `grep <id> | sed 's/.*Chief: //' | sort | uniq -c` — em vez de
olhar as últimas.

---

# Noite de 2026-08-03, ciclo 40 — três cadeados no mesmo card, um por vez

## O que você precisa saber antes de tocar em qualquer coisa

A esteira ficou **quatro horas parada e não era falta de conta**. Os seis assentos do Conselho
da fase 4 tinham entregue às 17:50Z e estavam `escalated`/`blocked`. Zero tentativa em
execução, zero aviso, e o supervisor relançando a Integradora a cada trinta minutos contra
uma sessão que morria em dois segundos com `You've hit your session limit`. Doze ciclos assim.

**Processo vivo não prova trabalho, e supervisor vivo não prova supervisão.** Se você
encontrar `integrator.yield` com `durationSeconds: 2` repetido no `EVENTS.jsonl`, não é a
Integradora desistindo: é a conta dela sem sessão. Olhe a hora do reset antes de procurar
defeito.

## A forma desta rodada: cadeados empilhados

Três defeitos independentes trancavam o MESMO card, e cada um só ficou visível depois que o
anterior saiu. Corrigir o primeiro não fez nada andar — **fez o sintoma mudar**, e foi a
troca de sintoma que provou que a correção tinha valido.

1. **`OPS-059a` — falta de revisor contava como falha.** O teto de quatro adiamentos com
   backoff de cinco minutos dá vinte minutos. A única outra conta com papel `critic` estava
   em resfriamento; os seis escalaram em oito minutos, com o texto da própria escalação
   dizendo *"o que falhou foi o revisor, não a entrega"*. A eleição já sabia distinguir
   "ninguém está livre agora" de "ninguém serve para isto" e devolvia a mesma lista vazia
   nos dois casos.
2. **`OPS-059b` — escalado, o card não tinha volta.** O replanejamento é o único caminho, e a
   precondição enumerava `rejected`/`cancelled`/`abandoned`. A tentativa parou em
   `awaiting_review`: entregou, ninguém reprovou porque ninguém revisou. `InvalidState`, para
   sempre. **Terceira vez** que a LISTA de estados fica mais estreita que a REGRA escrita ao
   lado dela — a regra dizia "o que não é replanejável é tentativa viva ou já aprovada", e
   agora o predicado nega exatamente esses dois.
3. **`OPS-060` — a chave de idempotência prometia estabilidade que a carga não tinha.** Tirado
   o `InvalidState`, o sintoma virou `conflito de idempotência`. A chave era
   card+versão+hash, mas o comando carrega também o id da nova versão de instrução: um ULID
   sorteado a cada rodada. Mesma chave, carga diferente. Como o inbox guarda **também as
   mutações recusadas**, a primeira recusa trancava todas as seguintes — inclusive as que já
   tinham a causa corrigida. A versão do card na chave era a defesa anterior e não alcança
   este caso: a versão só muda quando a mutação é APLICADA, e nenhuma era.

**Se você corrigir algo e o sintoma mudar em vez de sumir, não recue — avance.** Foi assim
que os três saíram em noventa minutos.

## O erro que eu cometi e corrigi na mesma hora

Troquei o teto de vinte minutos por uma **carência fixa de duas horas** — e o único crítico
elegível voltava em três. Os seis escalariam às 00:24, uma hora antes de a resposta poder
existir. É o erro simétrico de novo, e a saída era a de sempre nesta operação: **o provedor
DIZ quando volta**, e o dado já estava em `~/.harness/account-availability.json`. A espera
agora é `max(carência mínima, janela declarada)`, com teto de doze horas para que "ele disse
que volta" não vire espera sem fim (`OPS-061`).

Antes de endurecer OU afrouxar uma condição de parada, pergunte qual caso ela torna
impossível — e depois procure se o sistema já não sabe a resposta.

## Como está agora e o que acontece sozinho

Os seis pareceres re-executaram, entregaram e estão em `awaiting_review` **adiando, não
escalando**, porque o ator é `worker-antigravity-review` e o único outro crítico
(`worker-codex-critic`) está sem cota até **2026-08-04T01:21:25Z**. Isso **não é bloqueio do
proprietário e não é defeito**: é insumo com hora marcada, e a fase retoma sozinha.

Um review REAL aconteceu às 22:35Z (antigravity reprovou uma tentativa e o card entregou de
novo): o elo inteiro de revisão está provado de ponta a ponta, não só o caminho de falha.

**Só duas contas têm papel `critic`** e os cards do Conselho são executados por elas — então
uma serve de ator e sobra exatamente uma para revisar. Qualquer indisponibilidade de uma das
duas para a fase inteira. Se isso voltar a doer, o lugar é o elenco de papéis em
`~/.harness/agent-accounts.json`, não o código; e mexer em papel já derrubou uma fase inteira
antes (o caso `frontend-specialist` do handoff anterior). Pense duas vezes.

## Ferramenta que provou seu valor nesta rodada

`tools/operation/publish-when-idle.sh <project_id>` — usei três vezes em quarenta minutos,
sem perder uma tentativa. Ele pausa a esteira ANTES de conferir de novo, e essa ordem é a
razão de funcionar. Não reinicie o Host à mão enquanto ele existir.

---

# Noite de 2026-08-03, ciclo 41 — o cadeado que estava DEPOIS da cota

## Leia isto antes de comemorar a volta de uma conta

O ciclo 40 encerrou com um diagnóstico tranquilizador: os seis assentos do Conselho estavam
`awaiting_review` **adiando, não escalando**, à espera da cota de `worker-codex-critic`
(01:21:25Z). Correto — e incompleto. Havia um segundo cadeado, invisível porque o primeiro
o escondia: **mesmo depois do review, a fase 4 não fecharia.**

`AgentCouncilPolicy.FromExecution` deriva a opinião de cada conselheiro de
`work_attempts.summary`. Esse campo existe no contrato e **nenhum escritor o preenche**:
`WorkAttemptCompleteCommand` não tem sequer o parâmetro, e a mutação de conclusão atualiza
estado, duração, tokens e custo — nunca o resumo. No banco inteiro desta instalação:

```sh
sqlite3 ~/.harness-poseidon/harness.db \
  "select count(*) from work_attempts where summary is not null and trim(summary)<>''"
# 0
```

Zero. Em 363 tentativas só neste projeto, trinta delas aprovadas e mergeadas.

Com o card fechado e sem resumo, `FromExecution` devolvia `null`, o `continue` era **mudo**,
`opinions` ficava vazio e `Consolidate` respondia `council.incomplete` — para sempre. As
fases 5 a 9 e o eixo do produto gerado ficariam inalcançáveis, e o sintoma seria
indistinguível de "ainda não revisaram".

**A lição operacional:** quando um bloqueio tem hora marcada para sair, use o tempo para
perguntar *o que acontece no minuto seguinte*. O cadeado que você vê é o que está mais perto,
não o que está mais fundo. Foi assim no ciclo 40 (três cadeados empilhados) e de novo aqui.

## Onde a opinião mora de verdade

No **artefato**: `docs/conselho/<persona>-ciclo-<n>.md`, escrito sob claim estreito,
revisado e commitado. Conferido nos seis, com o marcador que a instrução exige:

```sh
git -C <repo-do-produto> show task/agent-run-<attempt-minusculo>:docs/conselho/<persona>-ciclo-1.md \
  | grep -m1 '^VEREDITO:'
```

Cinco `VEREDITO: LIBERAR` e um `RESSALVA` (playbook-arquiteto). A mensagem final do executor
não serve como fonte: ela só existe enquanto o processo está vivo, e o próprio comentário do
código já declarava que `snapshot.Execution` é nulo na colheita — que é o caso comum.

**A ordem das duas fontes importa e quase me pegou.** O conselho consolida *depois* que o
card fecha, e fechar inclui o merge. Nesse ponto `git diff HEAD...branch` já é **vazio** — a
base virou o próprio topo da branch. Ler só pela branch teria funcionado em todo teste manual
antes do merge e falhado exatamente no instante em que precisava funcionar. Por isso a
referência publicada vem primeiro, e a branch é o fallback. Há teste com git real cobrindo
os dois lados (`CouncilOpinionArtifactReaderGitTests`).

## Um achado que virou não-achado, e por que registro isso

Registrei `OPS-063` como crítico lendo o código: a fase 5 só cria card para objetivo do tipo
documento, e os cards de implementação dependem de `PromoteRequestForDevelopment`, que exige
uma **lista fechada de frases** ("quero um sistema", "portal", "site"…) que a demanda desta
prova não casa. Conferi no banco depois: **as três demandas já têm materialização registrada
com `surfaces.backend=true`**, e aquele método retorna cedo quando já existe superfície. A
lista de frases nunca é alcançada aqui. As três estão `pending` desde 02/08 apenas porque
`PlanMaterializationService` as adia com `workflow.development_not_released` enquanto a fase
ativa é menor que 5.

Fechei sem correção, com a evidência. **Ler o código diz o que PODE acontecer; ler o banco
diz o que VAI acontecer.** Um finding crítico errado custa a noite de quem vier depois.

## O que fica armado para a fase 5 — `OPS-064`

A camada `Deterministic` do review se declara "build, testes e varredura de segredo" e, na
ausência de veredito, é montada como `Pass` com `ReasonClean`. Os únicos alimentadores reais
são o gate documental e os diagnósticos Roslyn. O plano de hooks (`pre-commit scan-secrets`,
`pre-submit` testes) é gerado e serializado num JSON ao lado da worktree — e **não existe um
único leitor desse arquivo em todo o código**.

Isso nunca doeu porque **todo card até aqui foi de documento**, e o gate documental dava o
veredito. A fase 5 é a primeira que entrega código: um card de implementação seria aprovado
com "camada determinística limpa" sem que nada tivesse sido compilado, testado ou varrido.

**Não mexi no caminho de review de propósito** — é exatamente o código que roda às 01:21Z
para os seis pareceres. Mexer nele antes da transição mais importante da operação seria
trocar um risco conhecido por um desconhecido. A ordem está no `nextAction` do finding:
varredura de segredo e "camada sem veredito bloqueia" primeiro; execução de teste quando o
Briefing técnico da fase 5 declarar o stack.

## O que conferir quando a cota voltar

Por ID, nesta ordem — está tudo no `nextAction` do `STATE.json`:

1. os seis assentos saem de `awaiting_review`;
2. o log **para** de repetir `phase:phase-4:council:council.incomplete` e **não** aparece
   `council_opinion_missing:<persona>` (se aparecer, o artefato não foi lido — o problema é o
   caminho do arquivo, não o conselho);
3. o `gate-1` da fase 4 (`01KZ24JCJV2AQDN9A30Q1SF7Q3`) sai de `pending` e a fase 5 ativa;
4. as três materializações saem de `pending` e nascem cards de implementação.

O passo 2 é o que prova o `OPS-062`. Ele foi publicado às 23:10:51Z (binário `aa309902`), com
`publish-when-idle` — janela sem tentativa em voo, esteira pausada antes e retomada depois.
