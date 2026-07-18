# Evidência F2-DOC-1a — catálogo e versões documentais

Data: 2026-07-18. Commit funcional publicado: `cd50888` em `develop`.

Documentos e versões foram projetados diretamente das tabelas F1. A API oferece list/read/create para `documents` e `document-versions`, cursor e filtros por projeto/documento, sessão local e Problem Details. Os contratos contêm exatamente os campos TypeScript; estado relacional snake_case é convertido somente na borda.

O corpo não foi colocado no banco: `FileSystemDocumentContentCatalog` grava UTF-8 em path relativo gerado pelo backend, verifica SHA-256 antes da publicação e revalida o hash em toda leitura. Metadados, supersession e autoria continuam na autoridade `IDocumentStore`; falhas de mutação compensam o arquivo recém-gravado. O teste HTTP criou um documento órfão, normalizou classificações, anexou v2, releu v1/v2 e reiniciou o Host sobre o mesmo SQLite+catálogo sem perda.

`tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 134/134 testes (`Unit 87`, `Integration 24`, `Contract 10`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). Migrations permaneceram idempotentes em SQLite `16→0` e PostgreSQL `10→0`; `frontend/**` e `docs/frontend/**` não foram alterados.
