# Evidência — o produto gerado foi iniciado e testado (§49)

Medido em **2026-08-04T03:44–03:46Z**, ciclo 55, no repositório que a esteira do Poseidon
produziu sozinha:

```
~/.harness-poseidon/repositories/01KY36ED34GHB0NGGX718NSVK3/qa-prova-limpa-20260802-empres
HEAD 08a3c01 — merge(card FEAT/T01): tentativa 01KZ5BYQTZRPHZEG1QQF8SY982 aprovada em review
```

Este eixo estava `pending` desde o início da operação. Ele deixou de ser abstrato no instante em
que a fábrica escreveu código de verdade: até 03:23Z de hoje o repositório do produto tinha
**apenas `docs/`**, e não havia produto para iniciar.

## O que a entrega declarou, e por que isso importa

Stack escolhida pelo ator de backend: **Python da biblioteca padrão, zero dependência de
terceiro**, com os gates declarados em `tools/backend/build.sh` e `tools/backend/test.sh` —
espelhando a convenção do próprio repositório do Poseidon que ele estava lendo.

Isso expôs um defeito na correção do `OPS-071` que eu tinha acabado de publicar: o executor de
gates da plataforma só reconhecia `package.json`. A **única entrega aprovada da operação** teria
sido reportada como "não declara como executar" e bloqueada. Corrigido no mesmo ciclo: a
plataforma reconhece as duas formas de declaração, e há teste que executa cada uma de verdade.

## 1. Os gates da entrega, rodados pela plataforma

Ambiente mínimo (`env -i`), sem rede, sem nenhuma variável do Host:

```
$ bash tools/backend/build.sh
==> build: compilando src/
==> build: OK                                            exit 0

$ bash tools/backend/test.sh
Ran 42 tests in 0.010s
OK
==> tests: OK                                            exit 0
```

**42 testes do próprio produto, verdes.**

## 2. O produto no ar

```
$ EMPRESTIMOS_PORTA=8477 python3 -m emprestimos.app
emprestimos ouvindo em http://127.0.0.1:8477
```

Servidor WSGI da biblioteca padrão, banco SQLite real em disco.

## 3. Exercício de ponta a ponta pela API — o caminho feliz

| passo | requisição | resposta |
|---|---|---|
| cadastro de equipamento | `POST /equipamentos` | `201` — `{"id": 1, "identificacao": "NOTE-PROVA-001"}` |
| cadastro de membros | `POST /membros` ×2 | `201` — Ana (1), Bruno (2) |
| registrar empréstimo | `POST /emprestimos` com `X-Membro-Id: 2` | `201` — empréstimo 1, evento `EMPRESTIMO_REGISTRADO` |
| registrar devolução | `POST /emprestimos/1/devolucao` | `200` — evento `DEVOLUCAO_REGISTRADA`, `com_atraso: false` |
| histórico | `GET /emprestimos/1/eventos` | `200` — os **dois** eventos, com id, quem registrou e quando |

A rastreabilidade que o pedido original exigia está no dado: cada evento carrega
`registrado_por_id`, `ocorrido_em` e `registrado_em` separadamente.

## 4. As regras de negócio, exercidas contra o servidor no ar

Não são os testes do produto se auto-avaliando: são chamadas HTTP de fora, contra o processo
rodando, com a semântica de status correta.

| regra | resposta |
|---|---|
| equipamento com empréstimo em aberto não é emprestado de novo | `409 Conflict` — `equipamento_indisponivel` |
| responsável inexistente | `404 Not Found` — `nao_encontrado` |
| prazo anterior à data de retirada | `400 Bad Request` — `dados_invalidos` |

E o contrapeso, para não confundir tolerância com defeito: depois da devolução o **mesmo**
equipamento foi emprestado de novo e o sistema respondeu `201` — correto, ele estava livre.

## 5. Persistência real

```
sqlite_master → membro, equipamento, emprestimo, evento_emprestimo
```

Tabelas criadas pelo próprio produto; os eventos sobrevivem ao fim da requisição.

## O que isto prova e o que NÃO prova

**Prova:** a fábrica produziu software que compila, passa nos próprios testes, sobe, atende HTTP,
persiste e recusa o que a regra manda recusar — e a plataforma consegue verificar isso sozinha,
sem pedir ao agente a prova de uma execução que a sandbox dele proíbe.

**Não prova** que a fase 5 fecha: ela exige TODOS os cards da release em `merged`, e quatro cards
de implementação seguem `escalated` aguardando decisão do proprietário (`OPS-070`). Um card
entregue mostra que a fábrica CONSEGUE; não mostra que a esteira termina.
