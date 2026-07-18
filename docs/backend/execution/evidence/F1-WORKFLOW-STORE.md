# Evidência F1 — criação/publicação transacional de workflow

- Executado em: 2026-07-18T15:52:18Z
- Incremento: EP-10b.2a
- Resultado: verde

`IWorkflowStore` introduz um contrato provider-neutral para criar e publicar a versão inicial de uma definição em uma única transação e ler seu snapshot verificável. O comando carrega IDs ULID de definição, versão, fases, objetivos e gates; o validator exige ordem contínua, IDs/chaves únicos, pesos positivos, kinds fechados, gate pareado a objetivo `gate`, requisitos internos à fase e hash SHA-256 exato da hierarquia.

`SqliteWorkflowStore` usa o dispatcher único; `PostgresWorkflowStore` usa advisory lock por `(tenant, idempotencyKey)` e lock separado do ledger por tenant. Ambos gravam definição publicada, versão, fases, objetivos, gates, requisitos, Inbox, ledger encadeado e Outbox `workflow.definitionPublished` antes do commit.

O comportamento comum comprovou nos dois providers:

- 10 comandos concorrentes idênticos resultaram em exatamente 1 aplicação e 9 replays;
- todos os receipts preservaram o mesmo ledger hash e Outbox ID;
- snapshot recompôs status `published`, content hash, 1 fase, 2 objetivos, 1 gate e 1 requisito;
- mesma chave com payload diferente lançou `IdempotencyConflictException` sem alterar o snapshot;
- content hash divergente foi recusado antes da persistência;
- definição inexistente retornou ausência.

Gate integral: `tools/backend/verify.sh` exit code 0; restore locked/format verdes; build Release 0 warnings/0 errors; 84/84 testes (`Unit 55`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`). Cleanup final: zero containers, volumes ou networks com `com.harness.managed=true`.

Esta evidência não encerra EP-10b.2. O próximo incremento implementa criação e lifecycle transacional de `WorkflowRun`, optimistic concurrency, reidratação completa e cálculo de progresso diretamente das linhas objetivas.
