# ADR-008 — Estratégia de executores de agente

- Status: aceito
- Data: 2026-07-18

## Decisão

`IAgentExecutor` terá `FakeAgentExecutor` determinístico para testes, `CodexCliAgentExecutor` como primeiro executor real e `MicrosoftAgentFrameworkExecutor` para chefe/não codificadores. Testes reais exigem `HARNESS_RUN_REAL_AGENT_TESTS=true`.

## Consequências

Automação não consome rede/cota por padrão. Sessão ausente é reidratada por persistência, instrução, Git e artefatos. Outros adapters permanecem backlog até GNG-3 ou falha medida da PoC-4.
