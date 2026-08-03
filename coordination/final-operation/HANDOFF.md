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

## O que continua sendo do proprietário

`OPS-018` (Telegram), `OPS-028` (cota GLM), `OPS-029` (plano ChatGPT), `OPS-030` (credencial
Claude no Keychain). Nenhum deles é executável daqui — e a decisão do `OPS-030` é de
POLÍTICA, não técnica: extrair OAuth do Keychain para disco viola a regra de nunca persistir
segredo. Não a tome sozinho.
