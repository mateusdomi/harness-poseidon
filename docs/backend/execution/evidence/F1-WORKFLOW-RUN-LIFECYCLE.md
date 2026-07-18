# Evidência F1 — lifecycle transacional de WorkflowRun

- Executado em: 2026-07-18T16:09:05Z
- Incremento: EP-10b.2b.2
- Resultado: verde

`IWorkflowStore` expõe `TransitionRunAsync` para `start`, `pause`, `resume` e `cancel`, sempre com `expectedRunVersion` e chave de idempotência. O receipt distingue aplicação, replay, ausência, conflito de versão e estado inválido sem converter rejeições esperadas em exceções.

SQLite executa as mutações no dispatcher único. PostgreSQL serializa a chave idempotente e o run com advisory locks e lê a linha sob `FOR UPDATE`. O start ativa exatamente a primeira fase pendente na mesma transação; pausa/retomada preservam essa fase; cancelamento funciona tanto em pending quanto em execução e registra o encerramento UTC.

O comportamento comum comprovou nos dois providers:

- 10 starts concorrentes idênticos resultaram em 1 aplicação e 9 replays com o mesmo ledger hash e Outbox ID;
- o run avançou de pending v1 para running v2, com `startedAt` correto e exatamente uma fase ativa;
- comando stale retornou `VersionConflict` e seu replay determinístico retornou `IdempotentReplay` sem ledger ou Outbox;
- pause v3 e resume v4 foram aplicados sem perder a fase ativa;
- novo start em running retornou `InvalidState`; reutilização da chave com outro payload foi recusada;
- run inexistente retornou `NotFound` e um segundo run foi cancelado ainda pending, permanecendo sem fase ativa e com `completedAt` correto;
- somente transições aplicadas anexaram ledger e Outbox `progress.updated`; rejeições gravaram apenas o receipt na Inbox.

Durante a primeira execução PostgreSQL, o teste identificou placeholder posicional sem tipo em uma transição sem timestamp (`42P18`). O binding foi corrigido para placeholders contíguos; a repetição focada passou 1/1.

Gate integral: `tools/backend/verify.sh` exit code 0; restore locked/format verdes; build Release 0 warnings/0 errors; 84/84 testes (`Unit 55`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 13`). O inventário anterior ao fixture confirmou 11 containers de terceiros parados, 14 volumes, 7 networks e nenhuma colisão; cleanup final deixou zero recursos Harness.

O próximo incremento avança objetivos, avalia gates, conclui/ativa fases e reidrata a hierarquia completa com progresso ponderado calculado do banco.
