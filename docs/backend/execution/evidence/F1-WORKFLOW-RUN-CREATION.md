# Evidência F1 — inicialização transacional de WorkflowRun

- Executado em: 2026-07-18T15:59:52Z
- Incremento: EP-10b.2b.1
- Resultado: verde

`IWorkflowStore` agora cria um `WorkflowRun` a partir de uma versão publicada e lê um snapshot objetivo do estado persistido. O comando exige tenant, projeto, versão, run e chave de idempotência válidos; a versão precisa pertencer ao mesmo tenant/projeto e estar publicada.

`SqliteWorkflowStore` serializa a escrita no dispatcher único. `PostgresWorkflowStore` usa advisory lock por `(tenant, idempotencyKey)` e lock independente para o ledger. Ambos materializam run pendente, fases, objetivos e gates a partir da definição imutável e gravam Inbox, ledger encadeado e Outbox `progress.updated` antes do mesmo commit.

O comportamento comum comprovou nos dois providers:

- 10 comandos concorrentes idênticos resultaram em exatamente 1 aplicação e 9 replays;
- todos os receipts preservaram run versão 1, ledger hash e Outbox ID;
- snapshot recompôs estado `pending`, 1 fase, 2 objetivos, 1 gate e progresso executado/validado/aprovado `0/0/0` diretamente das projeções persistidas;
- a mesma chave com outro `runId` lançou `IdempotencyConflictException` sem alterar o run original;
- leitura de run inexistente retornou ausência;
- o teste PostgreSQL focado passou 1/1 após inventário Docker e o cleanup final deixou zero recursos com `com.harness.managed=true`.

Gate integral: `tools/backend/verify.sh` exit code 0; restore locked/format verdes; build Release 0 warnings/0 errors; 84/84 testes (`Unit 55`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`).

Esta evidência cobre somente a inicialização atômica e o snapshot agregado. O próximo incremento implementa transições com versão esperada, avanço de objetivos, avaliação de gates, progressão de fases, cancelamento e reidratação hierárquica completa.
