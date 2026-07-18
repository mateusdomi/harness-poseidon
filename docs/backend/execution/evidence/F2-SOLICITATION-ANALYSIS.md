# Evidência F2-PO-1 — análise de solicitação

Data: 2026-07-18. Commit funcional publicado: `9f865e9` em `develop`.

`POST /api/v1/solicitations/analyze` implementa o contrato FE-3 sem rede ou consumo de cota. O texto é normalizado e limitado; nomes de anexos são deduplicados, limitados e recusados quando contêm path. A análise local produz os cinco painéis tipados — requisitos, ambiguidades, contradições, perguntas e critérios de aceite — com IDs ULID, e cria uma solicitação pública `kind: request` cujo conteúdo permanece imutável.

O cenário HTTP comprovou recusa anônima, rejeição de path traversal em nome de anexo, geração dos cinco painéis, IDs canônicos, autoria pelo perfil local e leitura da solicitação após restart. O teste unitário cobre extração, termo ambíguo, contradição direta, pergunta explícita, deduplicação de anexo e validação adversarial. A criação de demanda permanece no CRUD de `demands`, exatamente como definido pelo handoff do frontend.

OpenAPI/drift coincidem com `AnalyzeSolicitationInput`, `SolicitationAnalysisItem` e `SolicitationAnalysis`. `tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 163/163 testes (`Unit 92`, `Integration 32`, `Contract 26`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
