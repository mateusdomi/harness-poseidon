# Refinamento v3 — arquivamento de tarefas

Data: 2026-07-19.

O primeiro desvio concreto encontrado entre o backend e o contrato frontend da FR-3 foi
fechado sem alterar `frontend/**` ou `docs/frontend/**`. `Task.archivedAt` agora é um metaestado
persistido, separado da máquina de estados do quadro: arquivar não muda `state`, não apaga a
tarefa e preserva instruções, tentativas, aprovações e histórico.

## Contrato e regras

- `POST /api/v1/tasks/{id}/archive` aceita somente tarefa em `done`;
- `POST /api/v1/tasks/{id}/unarchive` restaura a visibilidade sem mudar o estado;
- arquivar uma tarefa já arquivada, desarquivar uma ativa e arquivar antes de `done` retornam
  conflito 409 tipado;
- tarefa arquivada não pode ser movida no quadro antes de ser desarquivada;
- leitura individual e listagem retornam `archivedAt`, permitindo as visões ativa, arquivada e
  todas já implementadas client-side;
- cada alteração incrementa a versão e grava ledger + Outbox `task.stateChanged` no mesmo commit,
  com `archivedAt` e motivo `archived|unarchived` no payload.

## Persistência e retomada

As migrations `0035_task_archiving.sql` existem em SQLite e PostgreSQL, adicionam
`archived_at` e índice tenant/projeto/arquivamento/estado/id. O store usa o dispatcher único no
SQLite e `FOR UPDATE OF t` no PostgreSQL, portanto decisões concorrentes são serializadas e não
produzem auditoria falsa. O histórico de migrations avança 35→0 de forma idempotente e os testes
de upgrade por prefixo foram atualizados.

## Evidência executada

O cenário HTTP real comprovou sessão obrigatória, rejeições 409, ciclo
arquivar→duplicata→movimento recusado→desarquivar→duplicata→arquivar e recuperação de
`archivedAt` após restart. O drift test confere campo e endpoints contra o contrato TypeScript;
o OpenAPI canônico foi republicado. Testes focados: 28 contratos, 7 integração e 2 recovery,
todos verdes. O gate completo passou restore auditado, frontend 331/331, format, build Release
sem avisos/erros e backend 246/246; o SAST dedicado executou 30 regras sobre 316 arquivos C#,
aproximadamente 99,6% das linhas parseadas e zero achado.
