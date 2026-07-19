# V3 — catálogo tipado de esforço por modelo

Data: 2026-07-19. Resultado: verde.

A migration dual `0038_model_effort_mappings` acrescenta ao catálogo de modelos o mapeamento
explícito entre o nível canônico do Harness (`low`, `medium`, `high`, `max`) e o valor enviado ao
provider. OpenAI traduz `max` para `xhigh`; Anthropic e o provider local traduzem para o maior
valor atualmente suportado. Não existe passagem de uma string arbitrária sem mapeamento.

Os seeds lazy de novos tenants e o upgrade de linhas existentes recebem o mesmo catálogo. A API
expõe `effortMappings` como records C# tipados, sem alterar os nove campos já consumidos pelo
frontend. SQLite/PostgreSQL recompõem os mappings do JSON validado pelo schema.

Gates executados: HTTP 1/1, drift 5/5, PostgreSQL 1/1 com 38 migrations; `verify.sh` exit 0,
248/248 backend, 362/362 frontend e build Release sem avisos/erros; `verify-sast.sh` com 30 regras,
316 alvos C# e zero achado. `frontend/**` e `docs/frontend/**` permaneceram intactos.
