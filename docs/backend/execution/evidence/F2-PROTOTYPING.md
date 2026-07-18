# Evidência F2-PROT-1 — prototipação e referências visuais

Data: 2026-07-18. Commit funcional publicado após rebase: `2e47ea3` em `develop`.

O contrato de projeto agora inclui `prototyping` com os quatro cenários do frontend. `notApplicable` exige motivo e instante UTC do waiver; qualquer outro modo recusa waiver. A invariância existe no domínio e em triggers SQLite, incrementa `configVersion` e a própria store de protótipos bloqueia novas galerias enquanto a dispensa está ativa.

A migration `0024_prototyping` cria protótipos e referências visuais tenant/project-scoped. A API cobre list/get/create/delete dos dois recursos e lifecycle adicional de protótipo. Documento fonte e protótipo relacionado precisam pertencer ao projeto; tags são normalizadas; publicação exige URL; deletes são lógicos. Criação e transições gravam ledger, `prototype.created`/`prototype.stateChanged` são sequenciados no stream do projeto.

O cenário HTTP percorreu waiver inválido/válido, bloqueio pela dispensa, reativação, criação de protótipo/referência, deduplicação de tags, `draft→ready→published`, eventos e recuperação após restart. OpenAPI/drift coincidem com `Project`, `Prototype` e `VisualReference` existentes no frontend.

`tools/backend/verify.sh` passou antes e depois do rebase concorrente sobre `9bf6ffe`, com restore locked, format, build Release 0 warnings/0 errors e 159/159 testes (`Unit 91`, `Integration 30`, `Contract 25`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). SQLite ficou `24→0`, PostgreSQL `11→0`; as alterações remotas em `frontend/**` foram preservadas sem edição pelo backend.
