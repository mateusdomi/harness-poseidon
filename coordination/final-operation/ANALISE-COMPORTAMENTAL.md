# Análise comportamental do executor — origem de R1 e R2

Análise feita por outra LLM sobre o comportamento da instância Integradora, entregue pelo
proprietário em 2026-08-02 com a instrução de entender, implementar e testar. Está aqui
porque é a justificativa de tudo em `src/Modules/Harness.Modules.Operations/` — quem
continuar precisa saber **por que** o supervisor existe, não só que ele existe.

## O diagnóstico

> Os novos dados mudam a abordagem. O problema já não é falta de instrução. Você repetiu
> várias vezes que o Opus não deve parar, e ele mesmo reconheceu que violou essa regra.
> Agora apareceu um segundo problema igualmente importante: ele ficou quase dez minutos
> bloqueado em um polling mesmo quando a Bruna havia respondido em 24 segundos.

Duas falhas do **executor da auditoria**, não do Poseidon:

1. Opus encerra/yielda mesmo com trabalho conhecido.
2. Opus espera eventos de forma cega e pode ficar bloqueado enquanto o evento já aconteceu.

> Nenhum prompt maior resolverá isso de forma confiável. A solução correta é tirar essas
> duas responsabilidades do LLM.

## O problema da espera

O comando era aproximadamente:

```sh
until [[ "$(curl ... messages?limit=10 ... len(items))" -ge 2 ]]; do sleep 15; done
```

> Ele está perguntando "existem pelo menos duas mensagens?" quando deveria perguntar "o
> turno X que acabei de enviar foi respondido?". São coisas completamente diferentes.

E o shell ficou em foreground por mais de nove minutos. Enquanto bloqueado, o Opus não
investigava estado do turno, model invocation, worker, banco, outros cards, outros
projetos nem logs.

O correto é capturar `conversation_id`, `turn_id`, `submitted_at`, `correlation_id` e
acompanhar **aquele turno**: `queued → processing → completed → failed`; quando
`completed`, buscar a mensagem associada. Nunca `messages.count >= 2`.

**Regra proposta:** nenhum polling de observabilidade pode bloquear a instância Integradora
por mais de 30 segundos. Não vale para inferência comprovadamente ativa ou build real —
vale para espera de monitoramento.

## Os quatro estados

`running` sozinho é insuficiente. Precisamos de:

- **RUNNING** — atividade comprovável: PID, chamada de modelo, heartbeat, CPU, output,
  tool execution.
- **WAITING_OBSERVABLE** — evento futuro concreto: `turn_id = ABC`, `state = processing`,
  `model_invocation = active`.
- **WAITING_RESOURCE** — espera deliberada: slot HEAVY, repo lease, provider cooldown.
- **STALLED** — o estado diz que está trabalhando e não existe atividade real. Nesse caso:
  não esperar → diagnosticar → recuperar.

## A parada do Opus

> Prompt não é mecanismo de continuidade. Você pode escrever NÃO PARE cinquenta vezes.
> Ainda existe uma probabilidade de o modelo decidir "cheguei a um bom ponto de fechamento"
> e produzir POSEIDON — OPERAÇÃO FINAL. Ele acabou de fazer exatamente isso enquanto ainda
> conhecia 8 defects, E2E incompleto, fase 3/9 e dois cards falhando.
>
> Portanto: **o agente não pode mais decidir soberanamente que terminou.**

Arquitetura proposta:

```
OPUS → yield/resposta → Completion Gate DETERMINÍSTICO
                          ├─ terminou? → DONE
                          └─ não      → CONTINUE → relançar/resumir Opus
```

Se o Opus disser "relatório final" mas houver `open_executable_work > 0`, o sistema
interpreta como **YIELD**, não como **DONE**.

O gate precisa ser código:

```
CanOperationFinish =
    CleanE2E == PASS
    AND GeneratedProduct == PASS
    AND RecoveryTests == PASS
    AND BlockingFindings == 0
    AND ExecutableWork == 0
    AND MandatoryGatesFailed == 0
    AND MandatoryTestsPending == 0
```

Retornando false: `Agent exited → OperationSupervisor → resume/relaunch → continue`, sem
o proprietário escrever "continue".

## Externalização

> Não continuar aumentando o prompt atual, nem colar todos os prompts gigantes numa nova
> janela. **Externalizar especificação + estado + supervisor determinístico + nova sessão
> limpa.**

Estrutura: `coordination/final-operation/` com `OPERATION-SPEC.md` (ordem consolidada,
praticamente imutável), `STATE.json` (estado factual), `FINDINGS.jsonl` (um defeito por
linha, com `nextAction`, para o próximo Opus não reler 30 mil tokens), `EVENTS.jsonl`,
`METRICS.json`.

> O estado não deve ficar escondido no contexto do Claude.

## O supervisor

Antes de criar outro sistema, verificar se já existe equivalente em `continuation-worker`,
`night-supervisor`, `durable execution` ou `scheduler`. Se existir, reutilizar; se não,
criar mínimo — **preferencialmente dentro do ecossistema .NET do próprio projeto, para
funcionar futuramente também em Windows On-Premises**.

Responsabilidades: `load STATE → CompletionGate → há trabalho? → há Opus trabalhando? →
iniciar/resumir → monitorar → Opus saiu → CompletionGate de novo → ainda há trabalho? →
reabrir`.

Também controla os workers: `B stalled → recover B`; `I yielded + CompletionGate=false →
resume I`; `C working → não tocar`.

E resolve a saída do computador:

```
Mateus sai → Opus para → CompletionGate=false → Supervisor relança → continua
```

Se chegar a situação realmente humana (`HumanDecisionRequired=true`): Bruna →
Telegram/e-mail → Mateus responde remotamente. "Esse é exatamente o comportamento que você
quer do Poseidon."

## Correção sobre os 15,3 GB

> No macOS, 15,3 GB usados / 211 MB livres isoladamente não significa necessariamente
> pressão crítica. O macOS usa agressivamente RAM disponível para cache. O dado mais
> importante é memory_pressure, compressed memory, swap e pageouts. E o relatório disse
> swap = 0.
>
> Portanto, eu não concluiria apenas pelos 211 MB livres que dois workers eram o máximo. O
> Resource Coordinator deve usar memory pressure + swap activity + heavy processes, e não
> apenas free RAM. Isso pode permitir 3–4 agentes leves simultâneos, mantendo HEAVY=1.

## Os dois requisitos arquiteturais

**R1 — Agent exit is not authoritative.** Agente terminou ≠ operação terminou.

**R2 — Waiting must be observable.** "Estou esperando" só é válido quando existe alguma
coisa concreta sendo esperada.

> Eu não colocaria mais energia tentando aperfeiçoar a frase "não pare". A partir deste
> ponto, se um modelo parar cedo, o sistema deve simplesmente responder programaticamente:
> `CompletionGate = FAIL`, `Known executable work = 8 items`, "sua saída foi classificada
> como YIELD, continue" — sem você voltar ao computador.

## Prompt de bootstrap sugerido para a sessão nova

Está implementado em `BOOTSTRAP-PROMPT.md`, com o acréscimo do comando real do gate.
