# Evidência F1 — schema dual-provider de documentos

- Executado em: 2026-07-18T16:33:30Z
- Incremento: F1-DOC-1b
- Resultado: verde

As migrations SQLite `0006_documents` e PostgreSQL `0007_documents` criam cinco tabelas conceituais:

- `documents` para estado corrente, versão otimista, fase/classificação e inconsistência;
- `document_versions` para referências imutáveis de conteúdo;
- `document_classifications` para rótulos ordenados;
- `document_approval_requests` para fila, prioridade, prazo e resolução;
- `document_state_transitions` como histórico explícito append-only.

FKs compostas por tenant/projeto impedem vínculo cruzado. Índice parcial garante uma única aprovação pending por documento; índices cobrem catálogo de órfãos, versões, fila e timeline. Checks fecham kinds/estados/autores/prioridades e exigem nota/resolvedAt na rejeição/cancelamento.

Triggers provider-specific recusaram update/delete de `document_versions` e `document_state_transitions`. O cenário válido inseriu documento órfão, v1, classificação, aprovação pendente e transição; segunda pendente falhou por unicidade; mutação de versão e exclusão de histórico falharam; consulta de órfãos retornou 1.

Execuções focadas: SQLite 1/1 com migrations `6→0`; PostgreSQL 1/1 com migrations `7→0`, após inventário Docker. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build Release 0 warnings/0 errors e 92/92 testes (`Unit 63`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`). Cleanup final: zero recursos com `com.harness.managed=true`.

O próximo incremento implementa o store transacional e a reidratação completa, com Inbox, ledger e Outbox na mesma transação de cada mutação aplicada.
