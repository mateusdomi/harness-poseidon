# Workflow padrão de nove fases

Uma iniciativa percorre as nove fases abaixo. Cada transição exige card `gate` aprovado,
evidências válidas, condutor atual, próximo condutor, registro no ledger e comunicação da Bruna.

**Gates são Default-FAIL.** Gate não executado conta como reprovado, nunca como neutro; silêncio
nunca é aprovação. A transição de fase é um card `gate` aprovado — nunca uma passagem implícita
por tempo decorrido ou por ausência de objeção.

Este documento e a esteira materializada em `CanonicalWorkflowTemplates.PlaybookStandard` são a
mesma coisa dita duas vezes: o critério de cada gate abaixo é **literalmente** o que o banco
cobra, e um teste de paridade reprova se divergirem. Onde este texto e o banco discordarem, o
banco é corrigido na mesma entrega — o documento não é comentário sobre o produto, é o produto.

## O contrato Fixed/Flexible dos artefatos

Todo documento produzido por uma fase realiza um template do catálogo do playbook
(`workflow_document_templates`). O contrato é o mesmo para todos:

* **Fixo** — a existência das seções obrigatórias, a **ordem** em que aparecem e o formato das
  métricas. Um "lead time" ora em horas, ora em dias, ora em "rápido" não é métrica: é opinião com
  número.
* **Flexível** — a prosa, o vocabulário, o tom e a extensão de cada seção. O conteúdo é do autor.

A verificação acontece na criação do documento, contra os campos que o template declara. Julgar a
prosa transformaria o template numa camisa de força e produziria documentos que passam no gate sem
dizer nada.

## Como ler cada fase

**Condução** nomeia a especialidade líder (★) e os apoios. **Entradas** é o que precisa existir
para a fase começar; **Atividades** é o trabalho essencial; **Saídas** são os artefatos tipados
(template do catálogo ou card). **Gate** é o critério verificável — transcrito do que o banco
cobra. **Métricas** é o que a fase deixa medido para as fases seguintes e para os painéis.

---

## 1. Triagem

**Objetivo.** Decidir se a demanda entra, e por qual caminho. É a fase que protege as oito
seguintes: uma demanda mal qualificada custa barato aqui e caríssimo na Homologação.

**Condução.** `product-owner` ★. Apoio: `arquiteto` quando o caminho técnico for dúbio.

**Entradas.** Formulário de solicitação inicial (template `00`), na linguagem de quem pediu.

**Atividades.** Qualificar valor de negócio; atribuir criticidade; verificar viabilidade técnica
em traço grosso; decidir entre **Build, Buy, Integrate, Reuse ou Reject** e registrar o porquê.

**Saídas.** Ficha de Demanda Qualificada (`01`). Quando a decisão for recusar, Memorando de
Recusa (`02`) — recusar é decisão de produto e merece o mesmo rigor de aceitar.

**Gate (Default-FAIL).** Valor de negócio explícito, criticidade definida, caminho técnico viável
e decisão (Build/Buy/Integrate/Reuse/Reject) registrada com o porquê.

**Transições.** `2-Descoberta`; `Arquivada` quando Reject; `Roteada-para-Sustentação` quando for
demanda operacional e não projeto.

**Métricas.** Tempo até a decisão de triagem; proporção de demandas recusadas e o motivo
dominante — recusa alta com motivo repetido é sinal de intake mal desenhado, não de demanda ruim.

---

## 2. Descoberta

**Objetivo.** Transformar a demanda qualificada em problema compreendido e backlog refinável. Aqui
se descobre o que o usuário precisa, que raramente é igual ao que ele pediu.

**Condução.** `product-owner` ★. Apoio: `arquiteto` (viabilidade), `qa` (testabilidade dos
critérios), `security` (requisitos regulatórios).

**Entradas.** Ficha de Demanda Qualificada aprovada no gate 1.

**Atividades.** Entrevistas com roteiro (`02b`); PRD com objetivos **e não-objetivos** (`03`);
story map (`34b`); histórias INVEST com critérios em Gherkin (`04`, `36`); NFRs preliminares com
número (`35b`); levantamento de riscos.

**Saídas.** PRD, Story Map, NFRs preliminares.

**Gate (Default-FAIL).** Backlog refinável, histórias INVEST com critérios em Gherkin, NFRs
medíveis, riscos com mitigação e PRD aprovado pelo usuário.

**Métricas.** Percentual de histórias com critério em Gherkin; NFRs com número versus NFRs
adiados com dono declarado.

---

## 3. Arquitetura

**Objetivo.** Decidir a forma da solução e registrar o custo de cada decisão. A arquitetura não
escolhe "a melhor solução": escolhe trade-offs conscientes e documenta o que eles custam.

**Condução.** `arquiteto` ★ (coordena). Apoio: `security` (threat model ★ dentro da fase),
`dba-dados` (modelo de dados ★ dentro da fase), `tech-lead` (exequibilidade).

**Entradas.** PRD aprovado, NFRs, constraint profile do projeto.

**Atividades.** SAD com **visão ideal e visão restrita** (`06`) — a distância entre as duas é a
dívida que se aceita conscientemente; ADRs em formato MADR com consequências negativas explícitas
(`05`); C4 nos níveis Contexto e Contêiner (`07`); modelo de dados dirigido pelas queries reais
(`09`); comparativo de trade-off com critérios definidos **antes** das opções (`08`); threat model
STRIDE cobrindo OWASP:2025 (`10b`); plano de observabilidade com SLI e SLO calculáveis (`11b`).

**Saídas.** SAD Ideal e Restrito, ADRs, C4, DER, Comparativo de trade-off, Threat Model STRIDE,
Plano de Observabilidade.

**Gate (Default-FAIL).** ADRs aprovados por revisor distinto, NFRs medíveis, aderência ao
constraint profile (desvio exige ADR), threat model cobrindo OWASP:2025 e DER revisado.

**Métricas.** Número de ADRs com consequência negativa declarada; desvios do constraint profile
com ADR versus desvios sem ADR (o segundo grupo deve ser zero).

---

## 4. Planejamento

**Objetivo.** Tornar o trabalho despachável. Um card que chega ao executor sem DoR cumprida vira
pergunta, e pergunta vira rodada perdida.

**Condução.** `product-owner` ★ e `tech-lead` ★ (co-condução). Apoio: `arquiteto` (dependências
arquiteturais).

**Entradas.** Backlog refinado, SAD e ADRs aprovados.

**Atividades.** Refinar cards ao menor recorte seguro e verificável independentemente; escrever
DoR e DoD verificáveis por terceiro (`12`); estimar; montar cronograma de releases (`13`); mapear
riscos com gatilho de revisão (`14`); construir o grafo `provides`/`consumes` entre cards;
atribuir `risk_tier`.

**Saídas.** DoR e DoD, Cronograma de releases, Mapa de riscos e dependências.

**Gate (Default-FAIL).** Todo card com DoR cumprida, estimativa, dependências resolvidas ou
mapeadas e risk_tier atribuído.

**Métricas.** Percentual de cards que passam no linter de DoR sem retrabalho; profundidade máxima
da cadeia de dependências — cadeia longa é paralelismo que não vai acontecer.

---

## 5. Desenvolvimento

**Objetivo.** Construir. É a única fase em que N agentes trabalham em paralelo, cada um em
worktree isolada com `ScopeClaim` próprio.

**Condução.** `dev-executor` (N em paralelo). Revisão: `tech-lead` ★ — **revisor sempre distinto
do implementador**. Apoio: `security` e `dba-dados` por consulta.

**Entradas.** Cards prontos (DoR cumprida), briefing técnico por card (`18`).

**Atividades.** Implementar dentro do padrão arquitetural; testes junto do código; code review
estruturado nomeando camada e severidade (`15`); capturar ADRs incrementais que emergirem;
manter o dicionário ubíquo (`17`); registrar análise de incidente de desenvolvimento quando
houver (`19`).

**Saídas.** Briefing técnico, Code review estruturado, Métricas DORA (`16`), Dicionário ubíquo.

**Gate (Default-FAIL).** Todos os cards da release em Merged com integração verde; por card:
review aprovado por agente distinto, testes verdes, padrão arquitetural respeitado, sem segredo em
código e docs atualizados.

**Métricas.** DORA (lead time P50/P90, frequência de deploy, taxa de falha de mudança, MTTR);
pass@k por assinatura; distribuição MAST das falhas — é ela que diz se o problema é o enunciado,
a coordenação ou a verificação.

---

## 6. Testes

**Objetivo.** Provar que funciona e que continua funcionando sob carga, sob ataque e sob mudança.

**Condução.** `qa` ★. Apoio: `security` (pentest), `dba-dados` (volumetria e performance de
consulta), `devops` (ambiente).

**Entradas.** Release com todos os cards em `Merged`, plano de testes (`09b`).

**Atividades.** Testes funcionais por nível; performance com **percentis**, nunca média (`21`) —
a média esconde exatamente a cauda que derruba o usuário; pentest com evidência reproduzível
(`22`); consolidar o quality gate (`20`); emitir parecer Go/No-Go (`23`).

**Saídas.** Plano de Testes, Relatório de Quality Gate, Relatório de Performance, Relatório de
Pentest, Parecer Go/No-Go.

**Gate (Default-FAIL).** Quality gate verde, cobertura ≥ perfil do projeto, zero bugs P0/P1
abertos e parecer Go emitido.

**Métricas.** Cobertura por tipo de teste; latência P50/P95/P99; defeitos por severidade
encontrados nesta fase versus na Homologação — defeito que escapa para a 7 é falha da 6.

---

## 7. Homologação

**Objetivo.** O usuário-chave confirma que o que foi entregue resolve o que ele pediu. É gate
**humano** e não é delegável.

**Condução.** `product-owner` ★ e `qa` ★ (instrumentação). Executa: o usuário-chave.

**Entradas.** Parecer Go, ambiente espelho, roteiro de UAT (`10`) escrito para o usuário executar
sozinho — qualquer passo que precise de tradução por um técnico invalida o UAT como prova.

**Atividades.** Executar os cenários; registrar resultado **por cenário**, não agregado (`24`);
classificar defeitos entre bloqueadores do aceite e backlog (`26`); colher o Termo de Aceite
(`25`); comunicar a aprovação em linguagem de negócio (`34`).

**Saídas.** Roteiro UAT, Resultados UAT, Defeitos UAT, Termo de Aceite.

**Gate (Default-FAIL, HITL obrigatório).** Roteiro de UAT executado, zero P0/P1 abertos e Termo
de Aceite aprovado pelo humano (HITL obrigatório).

**Métricas.** Cenários executados versus previstos; defeitos que bloquearam o aceite.

---

## 8. Release

**Objetivo.** Colocar em produção de forma reversível. Rollback é parte do plano A, não plano B.

**Condução.** `devops` ★. Apoio: `sre-sustentacao` (critérios de saúde), `security` (SBOM).

**Entradas.** Termo de Aceite aprovado.

**Atividades.** Preparar a GMUD com janela e critérios de saúde objetivos (`11`); **ensaiar** o
rollback (`28`) — plano de rollback não testado é plano de esperança; gerar SBOM a partir da build
(`29`); escrever notas de versão para quem usa (`27`); revisar o runbook (`12b`).

**Saídas.** GMUD, Notas de versão, Plano de rollback, SBOM, Runbook revisado.

**Gate (Default-FAIL, HITL obrigatório).** Aprovação humana da mudança e da janela, rollback
testado em homologação, janela cumprida, métricas de saúde estáveis pós-deploy e documentação
atualizada.

**Métricas.** Tempo de execução da janela versus estimado; tempo de rollback medido no ensaio;
estabilidade das métricas de saúde na janela de observação declarada.

---

## 9. Sustentação

**Objetivo.** Manter o que foi entregue vivo e saudável, e transformar cada incidente em ação.

**Condução.** `sre-sustentacao` ★. Apoio: `security` (vulnerabilidades), `dba-dados` (capacidade
de dados), `devops` (infraestrutura).

**Entradas.** Sistema em produção, runbooks, SLOs declarados na Fase 3.

**Atividades.** Operar dentro do SLA; conduzir incidentes; postmortem **blameless** com ação, dono
e prazo (`13b`); manter runbooks vivos (`12b`); relatório mensal de operação (`30`); capacity
planning com premissa declarada (`31`); game days (`32`) e DR drills com RTO/RPO **alvo e medido**
lado a lado (`33`); comunicar em linguagem de negócio (`35`).

**Saídas.** Runbooks vivos, Postmortem blameless, Relatório mensal de operação, Capacity planning.

**Gate contínuo mensal (Default-FAIL).** Revisão mensal: chamados no SLA, incidentes com causa
raiz tratada, SLOs verdes e error budget controlado.

**Métricas.** Disponibilidade no período; SLA cumprido por severidade; consumo do error budget;
ações de postmortem concluídas dentro do prazo — postmortem cuja ação não fecha é postmortem que
não preveniu nada.

---

## Papéis e HITL

Bruna é a única voz com o usuário e **nunca executa**. As especialidades da fábrica são
`product-owner`, `arquiteto`, `tech-lead`, `qa`, `devops`, `sre-sustentacao`, `security`,
`dba-dados` e `dev-executor`; cada uma tem mentalidade, entregáveis, critérios de acionamento e
limites declarados em `docs/agents/`.

Revisor difere sempre do implementador. Dependem de humano: **Aceite UAT** (Fase 7), **mudança de
produção** (Fase 8) e **aceitação de risco residual** (qualquer fase). Silêncio nunca é aprovação.
