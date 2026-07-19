# Evidência F2-DOGFOOD-2a — materialização de demandas do Chief

Data: 2026-07-19.

O elo faltante entre o turno do Chief e a cadeia de trabalho foi fechado. `ChiefTurnCompleteCommand` aceita `ChiefDemandSeed` tipados (título, descrição, risk tier do conjunto fechado, critérios de aceite não vazios) e a conclusão do turno materializa cada proposta **na mesma transação** da completion: solicitação backing interna com o autor humano resolvido da mensagem do usuário, demanda `open` com critérios de aceite reais em JSON validado e prioridade igual ao risk tier, além de ledger encadeado e Outbox `demand.created` com payload tipado roteado ao stream do projeto. A idempotência da completion (lease/fencing + Inbox existentes) cobre a materialização — não há janela de perda entre completar o turno e criar as demandas.

`ChiefTurnBackgroundService` converte `ChiefTurnOutput.Demands` do executor em seeds com ULIDs derivados do instante da conclusão. O `FakeAgentExecutor` ganhou emissão determinística de propostas via marcador `DEMANDA: <título> | <descrição>` na instrução, sem alterar o comportamento dos cenários existentes.

O teste `ChiefDemandMaterializationTests` percorre o caminho real de dogfood por HTTP: turno de chat com duas propostas → worker assíncrono completa com Fake executor → `GET /api/v1/demands` devolve as duas demandas com título, descrição, prioridade `medium` e estado `open` → o stream `project:<id>` carrega dois `demand.created` → restart do Host preserva as demandas.

Gate: `dotnet format` sem mudanças; build Release com zero warnings/erros; backend 182/182 (`Unit 96`, `Integration 44`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` sem edição. Próximo: cenário dogfood ponta a ponta (solicitação→Chief→demanda→tarefa→execução isolada→critic/gate→auditoria) e smoke com Codex real.
