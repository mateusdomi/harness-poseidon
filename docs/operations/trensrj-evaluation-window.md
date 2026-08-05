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
| Prisma | _(preencher no kickoff)_ | — |
| Indicadores TrensRJ | _(preencher no kickoff)_ | — |

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
