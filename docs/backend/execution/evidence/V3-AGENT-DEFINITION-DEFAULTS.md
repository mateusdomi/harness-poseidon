# V3 — defaults operacionais das definições de agentes

Data: 2026-07-19

## Resultado

O ciclo de definições agora persiste e publica os campos avançados consumidos pelo orquestrador:
`stacks`, `defaultEffort`, `preferredAccountId`, `fallbackModelIds`, `team`, `actorCritic` e
`risk`. Esforço, papel actor/critic e risco usam conjuntos fechados; IDs são ULIDs canônicos;
stacks e fallbacks têm limites, unicidade e ordem preservada.

Conta preferencial e modelos alternativos são validados no tenant. Quando há conta preferencial,
modelo padrão e fallbacks precisam existir, estar habilitados e pertencer ao mesmo provider. A
remoção da conta é bloqueada enquanto uma definição a referencia.

## Persistência e evidência

- migration dual `0043_agent_definition_defaults`, com JSON validado, checks e índices;
- create/update/duplicate preservam os campos; snapshots vN também os incluem;
- HTTP/OpenAPI usam os mesmos nomes aditivos do frontend;
- behavior provider-neutral comprova valores, referências e histórico em SQLite/PostgreSQL;
- nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi alterado.
