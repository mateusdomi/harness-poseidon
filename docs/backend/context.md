# Context Builder

## Montagem server-side

O Context Builder recebe card, tentativa, agente, provider, workflow, fase, tipo,
risco, paths e orçamento. Ele carrega `load: always`, seleciona `load: bundle` por
escopo e inclui `load: on-demand` somente quando solicitado.

Depois do bundle documental, busca slices de memória autorizadas por FTS e
`IVectorIndex`. Resultados são ordenados por relevância e truncados dentro do
orçamento sem remover critérios de aceite, restrições, stop conditions ou
capabilities.

## Snapshot

Cada montagem persiste `work_task_id`, `execution_id`, ids do manifest, hash do
contexto montado, fontes, custo de tokens e timestamp. Um span registra a seleção e
um evento por documento, sem copiar conteúdo sensível.

## Invariantes

- O manifest é a única origem dos documentos selecionáveis.
- Conteúdo mantém proveniência e separação entre instrução e dado não confiável.
- Cache usa hash e é isolado por tenant e projeto.
- Handoff recebe o mínimo necessário.
- Falha de manifest, autorização, hash ou orçamento produz fallback bloqueante
  seguro, nunca contexto improvisado.
