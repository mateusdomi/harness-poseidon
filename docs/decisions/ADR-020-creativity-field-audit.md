# ADR-020 — Auditoria do campo "criatividade" e binding obrigatório

- Status: aceito
- Data: 2026-07-21

## Contexto

A homologação sinalizou um campo "criatividade" no fluxo de projeto e a
possibilidade de o valor "Crítica" ser usado sem definição inequívoca. A regra
de produto é: um campo que não altera execução real não pode existir apenas
visualmente; se altera comportamento, deve ter nome de domínio explícito, efeito
documentado e teste de binding.

## Auditoria

Varredura de `src/**` (código de produção, migrations e contratos backend):

- **Não existe** campo `criatividade`/`creativity` no domínio, nas migrations,
  nos contratos backend nem no OpenAPI. O conceito é, hoje, exclusivamente do
  frontend, sem binding no backend.
- O único conjunto fechado próximo é `criticality` do projeto
  (`low|medium|high|critical`), que **é o risk tier** e tem binding real e
  inequívoco: a partir de risco médio o par actor–critic é obrigatório
  (`WorkChain`), gates humanos são exigidos e a auto-aprovação é recusada. "critical"
  aqui significa criticidade/risco, não "criatividade".

## Decisão

1. `criticality` permanece como **risk tier** com definição inequívoca e binding
   já testado (actor–critic obrigatório ≥ médio; gates; Default-FAIL sem
   evidência). Não é renomeado.
2. O backend **não** introduz um campo "criatividade" apenas visual. Qualquer
   controle de "criatividade" na UI é tratado como um dos dois casos, nunca como
   campo cosmético:
   - **remover/deprecar** do fluxo, se não altera execução; ou
   - mapear para um conceito de domínio explícito — `AutonomyLevel` ou
     `ExplorationLevel` — com enum fechado, efeito documentado (políticas,
     prompts e, onde o provider suportar, `effort`/`temperature`) e teste de
     binding, antes de ser exposto.
3. Enquanto não houver binding backend definido, a recomendação de handoff ao
   frontend é **remover** o controle "criatividade" do golden path (não manter
   campo apenas visual). A introdução de `AutonomyLevel`/`ExplorationLevel` é um
   item de backlog que exige ADR próprio, migration, mapeamento por
   provider/model e testes de binding — só então volta ao fluxo.

## Consequências

- Fecha o risco de um valor ("Crítica"/"criatividade") sem semântica de execução
  aparecer como se controlasse o comportamento do agente.
- `criticality` continua sendo a única alavanca de risco, coerente com as regras
  de actor–critic e gates.
- Handoff: a decisão sobre remover ou mapear o controle na UI é do frontend,
  registrada no roteiro de handoff desta rodada; qualquer mapeamento futuro nasce
  com binding verificável, não visual.
