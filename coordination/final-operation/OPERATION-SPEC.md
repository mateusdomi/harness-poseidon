# Operação Final do Poseidon — especificação consolidada

Documento praticamente imutável. O que muda de execução para execução vive em
`STATE.json`, `FINDINGS.jsonl`, `EVENTS.jsonl` e `METRICS.json`.

## Objetivo soberano

Provar que um usuário humano leigo consegue descrever em prosa o sistema que deseja,
sair do computador, e confiar que Bruna Magalhães conduzirá sua equipe pelas nove fases
do Playbook até entregar um produto funcional, validado, rastreável e recuperável.

A prova vale mais que número de commits, testes, documentos ou board verde. Se o E2E não
provar a afirmação, o Poseidon não está concluído.

## Os dois princípios que governam esta operação

**R1 — A saída do agente não é autoritativa.** Agente terminou ≠ operação terminou. A
saída de uma instância Integradora é sempre um YIELD. Só o `CompletionGate`
determinístico decide se a operação acabou. Nenhuma frase em prompt substitui isso:
"não pare" repetido cinquenta vezes ainda deixa probabilidade de o modelo concluir que
chegou a um bom ponto de fechamento — foi exatamente o que aconteceu em 2026-08-02, com
8 defeitos conhecidos em aberto e o E2E na fase 3 de 9.

**R2 — Esperar precisa ser observável.** "Estou esperando" só é válido quando existe algo
concreto sendo esperado, identificado por ID. Contagem genérica não é observação: em
2026-08-02 a Integradora ficou 9 minutos bloqueada num laço que perguntava
"existem ao menos 2 mensagens?" enquanto a resposta da Bruna já existia havia 24
segundos, e durante esse tempo não investigou turno, invocação, worker, banco nem logs.

## Regras operacionais derivadas

1. Todo monitoramento acompanha um ID concreto: `turn_id`, `card_id`, `attempt_id`,
   `run_id`. Nunca `count(*) >= N`.
2. Nenhum polling de observabilidade bloqueia a Integradora por mais de **30 segundos**.
   O limite não se aplica a inferência comprovadamente ativa nem a build real.
3. Toda espera é classificada em `RUNNING`, `WAITING_OBSERVABLE`, `WAITING_RESOURCE` ou
   `STALLED`. `WAITING` genérico é proibido. `STALLED` nunca é esperado: é diagnosticado.
4. A pressão de máquina é medida por `memory_pressure`, memória comprimida, swap e
   pageouts — **não** por RAM livre. No macOS a RAM livre baixa é cache, não escassez.
5. `HEAVY` (build completo, suíte completa, Playwright, Docker build, verify, E2E,
   benchmark) tem concorrência 1, elevável a 2 só com medição de folga. Agentes `LIGHT`
   podem rodar 3–4 em paralelo.
6. Toda delegação continua sendo um card. Otimização não dissolve governança.
7. Workers não fazem merge em `develop`; só a Integradora integra.

## Condições legítimas de parada

Somente três:

- **A. Sucesso** — `CompletionGate` retorna PASS.
- **B. Cota/sessão esgotada** — commitar o que estiver consistente, nunca estado quebrado,
  gravar checkpoint em `STATE.json` e sair. O supervisor relança.
- **C. Bloqueio externo irresolvível** — credencial que só o proprietário gera, serviço
  fora, autorização humana obrigatória. Mesmo assim, todo trabalho que não depende do
  bloqueio continua. Um bloqueio não autoriza abandonar o resto.

## CompletionGate

A operação só termina quando **todas** as condições abaixo forem verdadeiras:

```
CleanE2E            == pass
GeneratedProduct    == pass
RecoveryTests       == pass
BlockingFindings    == 0
ExecutableWork      == 0
MandatoryGatesFailed== 0
MandatoryTestsPending == 0
```

Qualquer `false` classifica a saída da Integradora como `YIELD` e o
`OperationSupervisor` a relança.

## Como rodar o supervisor

Avaliar o gate (0 = PASS, 1 = FAIL):

```sh
dotnet run --project src/Harness.OperationSupervisor -- status
```

Deixar a operação andando sozinha — é isto que permite ao proprietário sair do
computador:

```sh
export POSEIDON_INTEGRATOR_COMMAND="claude -p --permission-mode bypassPermissions"
dotnet run --project src/Harness.OperationSupervisor -- run
```

O supervisor entrega `BOOTSTRAP-PROMPT.md` pela entrada padrão, espera a Integradora sair,
trata a saída como YIELD e relança enquanto o gate estiver em FAIL. Validado com sessão
real em 2026-08-02.

Variáveis: `POSEIDON_OPERATION_ROOT` (padrão `coordination/final-operation`),
`POSEIDON_SUPERVISOR_MAX_CYCLES` (padrão 100), `POSEIDON_SUPERVISOR_COOLDOWN_SECONDS`
(padrão 20).

## Fases que precisam ser provadas

`1 Triagem → 2 Descoberta → 3 Arquitetura → 4 Planejamento → CONSELHO → 5 Desenvolvimento
→ 6 Testes → 7 Homologação → 8 Release → 9 Sustentação`

Documento não conclui fase executiva. Fase 5 exige produto implementado; 6, testes
executados; 7, homologação efetiva conforme o modo; 8, release real; 9, transição de
sustentação comprovada.

## O E2E precisa passar pelo produto real

Proibido `INSERT` em SQL, chamada a endpoint interno ou marcação de fase para avançar.
Vale navegador, chat, Bruna, board, workflow, agentes e o produto gerado. Intervenção
manual só onde o próprio fluxo prevê HITL.

Ao final, **abrir o produto gerado**, executá-lo e compará-lo com a frase original do
usuário. "Projeto concluído" na tela do Poseidon não encerra nada.

## Rastreabilidade exigida

`intenção → requisito → proveniência → documento → decisão → card → profissional →
attempt → commit/artefato → review → evidência → entrega`

## Fora do caminho crítico

Não é obrigatório nesta operação: RAG novo, pgvector, GraphRAG, MCP novo, novos coding
agents, canais novos, fine-tuning, judge novo. Se já existem e quebram o E2E, corrigir;
senão, backlog. Expansão de feature não pode atrasar a prova do núcleo.
