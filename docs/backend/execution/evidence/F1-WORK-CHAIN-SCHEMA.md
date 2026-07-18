# Evidência F1 — schema da cadeia de trabalho

- Executado em: 2026-07-18T15:03:22Z
- Incremento: EP-09b.1
- Resultado: verde

Migrations independentes foram adicionadas aos dois providers: SQLite `0004_work_chain.sql` e PostgreSQL `0005_work_chain.sql`. Elas criam sete tabelas conceitualmente equivalentes: `solicitations`, `demands`, `work_tasks`, `instruction_versions`, `work_attempts`, `work_evidence` e `work_reviews`.

Invariantes materializadas no banco:

- FKs compostas carregam `tenant_id` e `project_id` por toda a cadeia, impedindo vínculo transversal acidental;
- solicitação pertence ao projeto e usuário do mesmo tenant;
- critérios de aceite são array JSON válido;
- risco, estados de tarefa/tentativa e decisão de review têm conjuntos fechados;
- instruções têm versão única por tarefa e `supersedes_id` restrito à mesma tarefa;
- tentativas referenciam instrução do mesmo tenant/projeto e número único por tarefa;
- índice parcial permite no máximo uma tentativa `running` por tarefa;
- evidências têm ordinal único e review é único por tentativa;
- índices suportam leitura por projeto, demanda, tarefa, estado e ordem de versões/attempts.

Os testes aplicaram migrations do zero (`SQLite 4`, `PostgreSQL 5`), repetiram com resultado zero, contaram as sete tabelas, inseriram uma cadeia completa e comprovaram violação de unicidade ao tentar inserir uma segunda tentativa ativa.

Durante o primeiro gate global, o PostgreSQL de Recovery marcou health antes de aceitar autenticação estável sob carga paralela e a primeira abertura terminou em `EndOfStream`. O fixture foi endurecido para exigir conexão Npgsql autenticada e `SELECT 1` com retry limitado antes de retornar. O segundo gate completo passou.

Evidência final: testes focados SQLite+PostgreSQL 2/2; `tools/backend/verify.sh` exit code 0; build Release 0 warnings/0 errors; suíte 73/73 (`Unit 46`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 11`). Cleanup deixou zero recursos Docker gerenciados.

Esta fatia valida somente o schema. `IWorkChainStore`, transações de aplicação, reidratação, Inbox, ledger e Outbox são o próximo incremento EP-09b.2.
