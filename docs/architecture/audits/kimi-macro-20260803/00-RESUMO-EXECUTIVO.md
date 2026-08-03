# 00 — Resumo executivo

**Auditoria macro do core, Bruna, workflow, agentes e execution plane**
**Data:** 2026-08-03 · **Branch:** `develop` · **SHA auditado:** `520dd71e`

---

## O que foi auditado, e como

Auditoria **estática e de estado**, sem consumir a conta da Chief. Toda afirmação foi verificada
em **código lido linha a linha**, **banco consultado** (`~/.harness-poseidon/harness.db`, 287 MB),
**configuração** (`~/.harness/*.json`, `poseidon.env`) e **eventos de tentativa**.
Os documentos existentes foram tratados como *input*, nunca como prova.

Cada capacidade foi classificada em **DOCUMENTADO / IMPLEMENTADO / TESTADO / PROVADO EM E2E** —
porque a distância entre `TESTADO` e `E2E` é onde mora quase tudo o que importa aqui.

---

## As cinco conclusões

### 1. O produto faz o que promete no eixo difícil, e isso está provado

**Nenhum bypass foi encontrado** para a garantia central — "o modelo não declara o próprio
trabalho pronto" — nos três níveis: card, fase e projeto. A independência actor≠critic é imposta
em **duas camadas** (escalonador e agregado de domínio). A recuperação de reinício do Host e de
morte de worker foi provada **ao vivo**, com zero circuito aberto após mais de 50 falhas de
infraestrutura. A cota tipada com reeleição automática foi provada em produção às 13:18Z de 03/08.

### 2. A fábrica nunca construiu nada

```
1-Triagem       completed        4-Planejamento  active (gate travado)
2-Descoberta    completed        5 a 9           pending
3-Arquitetura   completed
```

**Nenhuma linha de código de produto foi escrita pela frota.** O repositório da prova limpa tem
apenas `docs/`. As fases 5–9 estão implementadas e testadas, e **nunca executadas**.
Este é o fato central da auditoria.

### 3. O Conselho travou por invariante correta sobre elenco insuficiente

Os 6 pareceres **foram produzidos** (17:40–17:50Z), todos por `worker-antigravity-review` — a
única conta critic viva. A revisão independente exige conta critic **diferente do ator**; sobrava
`worker-codex-critic`, sem cota. A regra agiu certo. O que estava errado: escalar o card em
15 minutos, e depois não conseguir replanejá-lo.

**A causa estrutural:** o parecer do Conselho é um card comum, e cada assento consome uma conta
critic como ator. Com 2 contas critic, o Conselho é impossível sempre que uma delas para.

### 4. A Chief é single point of failure — e por um motivo evitável

Um único alias declara o papel `chief-orchestrator`, e o seletor da Chief
(`ResolveChiefAccount`, 9 linhas) **não usa o escalonador**: não consulta cota, cooldown nem
concorrência. Existem **três seletores de conta** no produto e só um é o auditado e testado.

Bruna **é** uma persona lógica, bem desacoplada de provedor no desenho (persona, governança e
contrato de saída são texto e schema; escopo pertence ao papel, nunca ao provider). O
acoplamento é de configuração e de adapter, não de conceito.

### 5. Modelo e effort não chegam à CLI

Nenhum run autônomo passa `--model` ou `--effort`. Banco e painel exibem `opus`/`medium`; a
execução usa o default do binário instalado. **O sistema não consegue responder de forma
centralizada qual modelo cada papel usa** — registrado como problema de governança.

---

## Respostas diretas às perguntas da ordem de auditoria

| Pergunta | Resposta |
|---|---|
| **Maior fase comprovada** | **3 — Arquitetura** (fase 4 com documentos validados e gate travado) |
| **Estado do Conselho** | 6 assentos `escalated/blocked`; causa completa em [08](08-CONSELHO-DE-AGENTES.md) |
| **Chief failover** | **NÃO SUPORTADO** na configuração; PARCIAL na arquitetura |
| **Trocar Bruna de Claude para Kimi** | **PARCIAL** — falta o adapter `kimi-code` e a capability `review`; feito isso, é edição de JSON + restart |
| **Scheduler de quota** | **PARTIAL** — excelente onde é usado, ignorado em dois caminhos |
| **Toda delegação é card?** | **SIM**, exceto a revisão (existe em `work_reviews`, não aparece no quadro) |
| **Quem declara DONE?** | **Sempre o código.** Nenhuma brecha encontrada |
| **Referências a TrensRJ** | **Zero** |
| **Contenção/sandbox** | `Disabled` por decisão registrada e reversível do proprietário |

---

## Achados

**27 achados** — 2 CRITICAL, 9 HIGH, 12 MEDIUM, 4 LOW.
**3 bloqueiam piloto** · **7 bloqueiam autonomia real**.

| ID | Sev | Título |
|---|---|---|
| `F-01` | **CRITICAL** | Chief é single point of failure; seletor próprio ignora cota |
| `F-03` | **CRITICAL** | O Conselho consome o próprio elenco de críticos |
| `F-02` | HIGH | Três seletores de conta com regras diferentes |
| `F-04` | HIGH | A Bruna nunca recebe `governance/core.md` — degrada em silêncio para 6 linhas |
| `F-05` | HIGH | Modelo e effort nunca chegam à CLI |
| `F-06` | HIGH | Taxonomia tipada pela metade — 10 classificadores por substring em caminho de decisão |
| `F-12` | HIGH | Falta de revisor escala o card em 15 min e o prende — **corrigido durante esta auditoria** (`e3c4025a`, `7ea4b60e`, `75971b37`); testado, falta publicar e provar |
| `F-13` | HIGH | `playbook-po` não existe no catálogo — o assento de Product Owner virou Software Architect |
| `F-16` | HIGH | Fases 6–9 são documentais com gate humano |
| `F-20` | HIGH | Não existe coordenador de recurso pesado |

Registro completo: [13-REGISTRO-DE-RISCOS](13-REGISTRO-DE-RISCOS.md).

---

## Readiness

| Eixo | /5 | | Eixo | /5 |
|---|---|---|---|---|
| Core Control Plane | **4** | | Council | **2** |
| Recovery | **4** | | Multi-project | **2** |
| Playbook | **4** | | Development | **1** |
| Scheduler | **4** | | Testing | **1** |
| Bruna | **3** | | Release | **1** |
| Execution Plane | **3** | | | |
| Observability | **3** | | | |
| Security | **3** | | | |

**Sem média.** A leitura correta é: **control plane maduro (4) acoplado a uma fábrica de software
não iniciada (1)**.

---

## Estimativa

| | Piloto utilizável | Autonomia real |
|---|---|---|
| **Melhor caso** | 3–5 dias | 6–8 semanas |
| **Provável** | 2–3 semanas | **3–4 meses** |
| **Pior caso** | 6–10 semanas | 8–12 meses |

**A métrica que decide o prazo dentro da faixa: a taxa de aceite de primeira, hoje em 33%**, com
73% do custo em retrabalho.

---

## As duas decisões que a próxima sessão precisa tomar

1. **O parecer do Conselho precisa de revisão independente?**
   Três opções com trade-offs em [17-RECOMENDACOES](17-RECOMENDACOES-ARQUITETURAIS.md) (`R-01`).
   Recomendação: isentar cards `council` — a consolidação já é o controle.

2. **Unificar os três seletores de conta num só?**
   ~40 linhas em 2 arquivos, e a Chief ganha failover de graça (`R-04`).

Feitas essas duas, mais publicar o conserto que já está escrito (`R-02`), a fase 4 fecha e
**a fase 5 roda pela primeira vez** — que é o único jeito de transformar a maior incógnita do
projeto em dado medido.

---

## Referência remota

```
REMOTE REVIEW REF:  origin/audit/kimi-macro-20260803
SHA:                32802eea52c8675e75f8d6189946af816766a9d4
BASE AUDITADA:      520dd71e9e383f7dcc389d4f6fb4a2c04775f658
```

Qualquer LLM pode consultar **exatamente** o código auditado nessa ref.

**Por que não `origin/develop`:** o `develop` local está 213 commits à frente e 0 atrás de
`origin/develop` — ou seja, um *fast-forward* seguro, sem force. Mesmo assim **não movi o ref**:
`STATE.json` registra `pushedToOrigin: false` e há outra sessão viva no repositório. Publicar 213
commits de trabalho alheio em `develop` é decisão do proprietário, não da auditoria. A branch
`audit/kimi-macro-20260803` carrega **os mesmos objetos**, então a revisão remota não perde nada.
Para promover: `git push origin develop` (fast-forward, sem `--force`).

**Preservação de trabalho:** nada foi resetado, sobrescrito ou descartado. O conserto de `F-12`,
que estava no working tree quando a auditoria começou, foi commitado por outra sessão viva
durante o trabalho (`e3c4025a`, `7ea4b60e`, `75971b37`) e está incluído nesta ref.

---

## Os documentos

| # | Documento |
|---|---|
| 00 | Resumo executivo *(este)* |
| 01 | [Fluxo end-to-end real](01-FLUXO-END-TO-END-REAL.md) |
| 02 | [Bruna: arquitetura e comportamento](02-BRUNA-ARQUITETURA-E-COMPORTAMENTO.md) |
| 03 | [Fontes da verdade e configuração](03-SOURCE-OF-TRUTH-E-CONFIGURACAO.md) |
| 04 | [Playbook e gates](04-PLAYBOOK-E-GATES.md) |
| 05 | [Personas e colaboradores](05-PERSONAS-E-COLABORADORES.md) |
| 06 | [Providers, contas, modelos e effort](06-PROVIDERS-CONTAS-MODELOS-E-EFFORT.md) |
| 07 | [Scheduler, cotas e failover](07-SCHEDULER-COTAS-E-FAILOVER.md) |
| 08 | [Conselho de Agentes](08-CONSELHO-DE-AGENTES.md) |
| 09 | [Contexto, memória e RAG](09-CONTEXTO-MEMORIA-RAG.md) |
| 10 | [Execution plane](10-EXECUTION-PLANE.md) |
| 11 | [Recuperação e resiliência](11-RECOVERY-E-RESILIENCIA.md) |
| 12 | [Estado atual do E2E](12-ESTADO-ATUAL-DO-E2E.md) |
| 13 | [Registro de riscos](13-REGISTRO-DE-RISCOS.md) |
| 14 | [Readiness e prazo](14-READINESS-E-PRAZO.md) |
| 15 | **[Guia: como alterar o comportamento](15-GUIA-COMO-ALTERAR-O-COMPORTAMENTO.md)** |
| 16 | [Mapa de fontes da verdade](16-MAPA-DE-FONTES-DA-VERDADE.md) |
| 17 | [Recomendações arquiteturais](17-RECOMENDACOES-ARQUITETURAIS.md) |
| 18 | **[Visão para gestão](18-VISAO-PARA-GESTAO.md)** |
| 19 | [Mapa dos 30 projetos da solution](19-MAPA-DOS-PROJETOS-DA-SOLUTION.md) |
