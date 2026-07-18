# Evidência F2-OPS-1 — backup, restore e diagnóstico local

Data: 2026-07-18. Commit funcional publicado: `b1edd80` em `develop`.

`POST /api/v1/backups` cria um snapshot online do SQLite serializado pelo dispatcher, copia o catálogo de documentos sem atravessar links simbólicos e publica o diretório somente depois de concluído. A raiz fica ao lado do banco e cada caminho é derivado de ULID canônico com verificação de confinamento. `POST /backups/{id}/restore` guarda rollback temporário do banco e do catálogo, restaura ambos e registra `backup.restored`; temporários são removidos ao final.

O cenário HTTP criou projeto, gerou backup real, criou outro projeto depois do snapshot e restaurou. O estado anterior permaneceu e a entidade posterior desapareceu; ID inválido e backup ausente foram recusados, e a auditoria global de restore foi entregue. `GET /diagnostics` exige sessão e retorna Poseidon 0.3.0/Harness, modo HTTP, disponibilidade realtime e checks reais de `PRAGMA quick_check`, catálogo e diretório de backups.

OpenAPI/drift coincidem com `BackupHandle`, `DiagnosticCheck` e `Diagnostics`. `tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 168/168 testes (`Unit 93`, `Integration 34`, `Contract 28`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
