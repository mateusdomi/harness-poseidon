# Evidência F2-GOV-1 — governança e auditoria

Data: 2026-07-18. Commit funcional publicado: `bcb71b9` em `develop`.

`audit-events` usa o ledger encadeado como única autoridade e projeta o contrato exato do frontend. Payloads que já contêm `auditEvent` preservam ator, ação, alvo e detalhe; entradas históricas são convertidas deterministicamente com ator `system` quando não há autoridade explícita, sem devolver o payload bruto. List/get, cursor e filtros por ator, ação, alvo e período são isolados pelo tenant da sessão.

`GET /audit-events/integrity` percorre a cadeia inteira, exige sequência contígua e recalcula `previousHash`/SHA-256 canônico. As migrations `0023_audit_ledger_append_only` e `0011_audit_ledger_append_only` recusam UPDATE/DELETE no SQLite e PostgreSQL. Os testes tentaram mutação real nos dois providers e comprovaram a recusa, mantendo a cadeia válida após restart.

`GET /audit-events/export` gera JSON ou CSV determinísticos com os mesmos oito campos da UI. Detalhes passam por mascaramento de nomes sensíveis e tokens `sk/rk/pk` antes de sair do Host; o cenário adversarial inseriu um segredo em uma entrada válida e comprovou sua ausência nos dois formatos. OpenAPI/drift cobrem o contrato e as rotas de list/get/integrity/export.

`tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 156/156 testes (`Unit 91`, `Integration 29`, `Contract 23`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). Migrations ficaram idempotentes em SQLite `23→0` e PostgreSQL `11→0`; `frontend/**` e `docs/frontend/**` permaneceram intactos.
