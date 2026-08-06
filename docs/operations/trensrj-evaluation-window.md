# TrensRJ Poseidon Evaluation 2026 — janela oficial de avaliação empresarial

**Início:** 2026-08-05 · **Deadline:** 2026-08-20 · **Projetos:** Prisma e Indicadores TrensRJ
(nome provisório) · **Supervisor único:** Mateus.

> A partir desta linha começa o experimento oficial. Tudo o que os relatórios do dia 20
> apresentarem pertence ao desenvolvimento REAL dos dois produtos pelo Poseidon —
> `timestamp >= 2026-08-05T00:00-03:00 AND ProjectId IN (projetos da avaliação)` —
> pelo recorte determinístico `EvaluationWindow.Includes` (Modules.Workflows.Product).

## Baseline da plataforma

- **SHA do Poseidon no fechamento do gate:** registrado no commit desta missão em
  `origin/develop` (o SHA do commit que contém este documento é o baseline).
- Flag `graph.projection.enabled`: **ON nesta instalação** (default do produto segue OFF).
- Fairness de portfólio, isolamento multi-projeto, isolamento de schema Oracle
  (`PRISMA_APP` × `INDICADORES_APP`), Human Attention Loop e intake agnóstico de template:
  todos provados por teste antes desta linha.

## Projetos da avaliação

Os objetos de PREFLIGHT (`01KZ7ZNTXRRQMMA9G1TQ2H5BFW` Prisma, `01KZ9MWXYXJZCJ0VAMWQHAQHDC`
Indicadores) foram **arquivados** (snapshot forense completo em
`Arquivos-Historicos-Projetos/preflight-avaliacao-20260805/`, incluindo grafo e anexos) e
**soft-deletados** pelo fluxo oficial. Nenhuma tentativa de agente foi executada neles.

Os projetos FINAIS da avaliação serão recriados pelo caminho oficial do navegador no kickoff,
**pristine**, e seus ProjectIds registrados abaixo no ato:

| Projeto | ProjectId final | Criado em |
|---|---|---|
| Prisma | `01KZA9D9BQFD72D1E2FNN7SD70` | 2026-08-05 (pristine; spec sha A337F909…, ZIP Lovable sha E737EC41… como provided_frontend; intake READY 26 critérios/0 bloqueios) |
| Indicadores TrensRJ | `01KZA9EQ9JE0YV4P7E8B39TN7S` | 2026-08-05 (pristine; levantamento sha D42430AD…, ZIP Lovable `beautifully-crafted-interface-main` sha FFD5864F… como provided_frontend, 2 HTMLs design_reference; intake READY 22 critérios/0 bloqueios) |

### Kickoff oficial (execução sobreposta iniciada)

| Projeto | Kickoff enviado (UTC) | Despausado | Primeira resposta da Bruna |
|---|---|---|---|
| Prisma | 2026-08-06T01:02:58Z | 01:04Z (`active`) | 01:08Z — Triagem, 8 demandas registradas, 0 ASK bloqueante (prazo classificado como DEFER) |
| Indicadores TrensRJ | 2026-08-06T01:40:40Z | 01:42Z (`active`) | (registrar) |

Checklist de observação dos 30 minutos do Prisma: PASS — sem re-entrevista, Oracle 19c
registrado como decisão de arquitetura, frontend do Lovable reconhecido como base oficial,
premissas reversíveis declaradas, grafo isolado por projeto, Indicadores intocado até o
próprio kickoff.

> Ajuste administrativo registrado: os registros SOFT-DELETADOS de preflight foram renomeados
> ("… (preflight arquivado 05/08)", keys PRISMAPRE/INDICPRE) para liberar a unicidade de nome na
> org — dado, não código. Observação para o backlog pós-avaliação: a unicidade de nome/key de
> projeto não ignora soft-deletados (descoberto operacionalmente; sem patch durante a janela).

### Runbook de recriação (kickoff)

1. Navegador → Modo Técnico → Projetos → Novo projeto → org **TrensRJ**, criticidade
   **Crítica**, prazo **20/08/2026** (nasce `paused`).
2. Prisma: anexar `Prisma_Especificacao_Tecnica_MVP.md` (`requirements_source`) e
   `bright-vision-interface-main.zip` (`provided_frontend`) — ambos em `~/Downloads`.
3. Indicadores: anexar `Texto colado(20260805-181531).txt` (`requirements_source`), os dois
   HTMLs (`design_reference`) e, se disponível, o ZIP React do Lovable (`provided_frontend`).
4. Conferir `GET /api/v1/solicitations/{id}/intake-status` = **READY** nos dois.
5. Registrar os ProjectIds finais NESTA tabela e no relatório.
6. Enviar a mensagem de kickoff do Prisma; pouco depois, a do Indicadores — a SOBREPOSIÇÃO é
   parte da prova.

## Métricas do dia 20 (todas deriváveis do ledger, recortadas pela janela)

Por projeto: pedidos de decisão humana e mediana de tempo de resposta
(`human_attention_requests.created_at → answered_at`), mensagens humanas, execuções de agente
(`model_invocations`), cards concluídos, correções, reviews, testes, evidence sets, duração de
fases. Portfólio: **duração de execução sobreposta** entre os dois projetos. Nenhum dado
fabricado — se a métrica não deriva do ledger, ela não entra no relatório.
