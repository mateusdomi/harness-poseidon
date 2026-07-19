# V3 — ciclo de rascunho e publicação de workflows

Data: 2026-07-19. Branch: `develop`.

## Entrega

- `POST /api/v1/workflow-templates` cria um template em `draft` quando o payload não contém a hierarquia legada; templates existentes com fases continuam compatíveis e publicados.
- `POST /api/v1/workflow-templates/{id}/drafts` cria uma versão monotônica editável; payload vazio copia a versão publicada vigente e template sem versão recebe um rascunho vazio.
- `PATCH /api/v1/workflow-versions/{id}` faz atualização parcial somente de rascunho. Versões publicadas são imutáveis e retornam 409.
- `POST /api/v1/workflow-versions/{id}/publish` valida a hierarquia e referências tipadas antes de publicar. Rascunho inválido retorna 422; publicação define a versão vigente e emite `workflow.versionPublished` atomicamente com ledger e Outbox.
- Contratos de fase foram ampliados com objetivo, contexto, critérios de aceite, dependências, entry/exit, skills e tools. Dependências desconhecidas/cíclicas e ULIDs inválidos de skills/tools são recusados.
- `POST /workflow-templates/{id}/archive` e `POST /workflow-versions/{id}/archive` aplicam tombstone; versão vigente não pode ser arquivada e recursos já arquivados retornam 409.
- `DELETE /workflow-versions/{id}` remove somente rascunho ativo nunca utilizado. `DELETE /workflow-templates/{id}` remove somente template rascunho sem publicação/vínculo e elimina seus rascunhos na mesma transação.
- Duplicação de versão cria novo rascunho monotônico no mesmo template. Duplicação de template é atômica, cria `" (cópia)"` em rascunho e copia somente a versão vigente como rascunho v1 com IDs novos.
- `POST /projects/{id}/workflow` vincula uma única vez, aceita versão publicada explícita ou a vigente, herda `defaultOperationMode` (fallback `manual`) e publica auditoria `workflow.templateLinked`; templates/versões arquivados são recusados.
- Migration dual `0036_workflow_draft_lifecycle` adiciona os tombstones sem alterar dados publicados existentes.

SQLite e PostgreSQL implementam as mesmas transações, regras de estado, numeração monotônica e leitura de `draft`/`published`/`archived`. A bateria provider-neutra percorre criação, vínculo, tombstones, exclusões e duplicação atômica nos dois bancos. O teste HTTP cobre criação mínima, cópia de versão, edição parcial, rejeição 422, imutabilidade 409, publicação v3, archive/delete/duplicate, vínculo por projeto, evento global e reinício do Host.

## Gates executados

- `tools/backend/export-contracts.sh`: OpenAPI canônico republicado.
- `WorkflowContractDriftTests`: 1/1 verde.
- `WorkflowApiTests`: 1/1 verde.
- `PostgresSkipLockedPocTests`: 1/1 verde, incluindo reaplicação idempotente das 36 migrations.
- `tools/backend/verify.sh`: exit 0; build Release com 0 avisos/0 erros; 248/248 backend (`123` unit, `82` integration, `28` contract, `7` architecture, `5` recovery, `3` concurrency) e 362/362 frontend.
- `tools/backend/verify-sast.sh`: 30 regras, 316 alvos, zero achado bloqueante.

O gate de frontend foi executado sobre a cópia isolada criada pelo script. Nenhum arquivo em `frontend/**` ou `docs/frontend/**` foi modificado nesta entrega.

## Resultado

O contrato funcional FR-4 descrito em `docs/frontend/HANDOFF_API.md` está completo no backend. Permanecem fora deste contrato as pendências explicitamente registradas pelo frontend (troca do template de um workflow já vinculado e troca de `activeVersionId`), que exigem comandos novos antes de implementação.
