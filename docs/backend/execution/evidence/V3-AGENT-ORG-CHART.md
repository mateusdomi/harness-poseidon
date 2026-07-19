# Refinamento v3 — read-model de organograma de agentes

Data: 2026-07-19.

O endpoint `GET /api/v1/projects/{projectId}/agent-org-chart` materializa a hierarquia operacional
do projeto sem criar uma segunda autoridade. Projeto, Chief vigente, instâncias, definições,
capacidades, modelo efetivo, estado e métricas são lidos dos stores já autoritativos.

## Propriedades do read-model

- sessão e tenant são resolvidos antes de qualquer leitura; projeto de outro tenant responde 404;
- o `rootAgentId` é sempre o `chiefAgentId` vigente do projeto, não apenas qualquer definição com
  role `chief`;
- após handoff, o novo Chief ocupa nível 0 e instâncias anteriores permanecem no histórico visual
  como filhos de nível 1;
- cada nó expõe parent, level e order determinísticos, identidade da definição, role/specialty,
  state/currentTask, `effectiveModelId` (override da instância ou default da definição), skills,
  tools e métricas acumuladas;
- ordenação é estável (root, role, nome ordinal, id) e a leitura é paginada internamente em blocos
  de 200, com limite de segurança fechado em 2.000 nós;
- definição ausente falha com 409 em vez de inventar capacidade ou devolver organograma parcial.

## Evidência executada

O teste HTTP de projetos comprova root único, nível/order, role, estado, skills, modelo efetivo e
recuperação após restart. O cenário de handoff comprova dois Chiefs históricos com a nova
instância como raiz e a anterior ligada como filha. O OpenAPI canônico inclui contratos tipados
do chart/nó e os dois testes focados de integração mais dois de contrato/drift estão verdes. O
gate integral passou frontend 331/331, build Release sem avisos/erros e backend 246/246; o SAST
dedicado executou 30 regras sobre 316 arquivos C#, aproximadamente 99,6% das linhas parseadas e
zero achado.
