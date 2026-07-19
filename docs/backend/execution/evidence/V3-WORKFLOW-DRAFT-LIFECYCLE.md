# V3 — ciclo de rascunho e publicação de workflows

Data: 2026-07-19. Branch: `develop`.

## Entrega

- `POST /api/v1/workflow-templates` cria um template em `draft` quando o payload não contém a hierarquia legada; templates existentes com fases continuam compatíveis e publicados.
- `POST /api/v1/workflow-templates/{id}/drafts` cria uma versão monotônica editável; payload vazio copia a versão publicada vigente e template sem versão recebe um rascunho vazio.
- `PATCH /api/v1/workflow-versions/{id}` faz atualização parcial somente de rascunho. Versões publicadas são imutáveis e retornam 409.
- `POST /api/v1/workflow-versions/{id}/publish` valida a hierarquia e referências tipadas antes de publicar. Rascunho inválido retorna 422; publicação define a versão vigente e emite `workflow.versionPublished` atomicamente com ledger e Outbox.
- Contratos de fase foram ampliados com objetivo, contexto, critérios de aceite, dependências, entry/exit, skills e tools. Dependências desconhecidas/cíclicas e ULIDs inválidos de skills/tools são recusados.
- Migration dual `0036_workflow_draft_lifecycle` adiciona tombstones preparados para a próxima fatia de archive/delete, sem alterar dados publicados existentes.

SQLite e PostgreSQL implementam as mesmas transações, regras de estado, numeração monotônica e leitura de `draft`/`published`/`archived`. A bateria provider-neutra cria template e versão em rascunho nos dois bancos. O teste HTTP cobre criação mínima, cópia de versão, edição parcial, rejeição 422, imutabilidade 409, publicação v3, evento global e reinício do Host.

## Gates executados

- `tools/backend/export-contracts.sh`: OpenAPI canônico republicado.
- `WorkflowContractDriftTests`: 1/1 verde.
- `WorkflowApiTests`: 1/1 verde.
- `PostgresSkipLockedPocTests`: 1/1 verde, incluindo reaplicação idempotente das 36 migrations.
- `tools/backend/verify.sh`: exit 0; build Release com 0 avisos/0 erros; 248/248 backend (`123` unit, `82` integration, `28` contract, `7` architecture, `5` recovery, `3` concurrency) e 362/362 frontend.
- `tools/backend/verify-sast.sh`: 30 regras, 316 alvos, zero achado bloqueante.

O gate de frontend foi executado sobre a cópia isolada criada pelo script. Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado nesta entrega.

## Próxima fatia independente

Completar os comandos de arquivamento, exclusão segura e duplicação de template/versão, seguidos do vínculo de workflow a projeto conforme o contrato FR-4.
