# Evidência F1 — mutações transacionais da cadeia

- Executado em: 2026-07-18T15:21:22Z
- Incremento: EP-09b.2b.1
- Resultado: verde

`IWorkChainStore` agora oferece comandos tipados para iniciar tentativa, concluir uma tentativa com evidências e registrar a revisão. Cada comando carrega a versão esperada da tarefa e uma chave de idempotência. O receipt informa o resultado (`Applied`, replay, conflito de versão, estado inválido, ausência ou reviewer independente obrigatório), a nova versão e, quando aplicado, os identificadores do ledger e da Outbox.

SQLite executa cada mutação pelo dispatcher único e em uma transação explícita. PostgreSQL serializa a tarefa com advisory lock, bloqueia a linha com `FOR UPDATE` e mantém a serialização do ledger por tenant. Nos dois providers, estado, Inbox, ledger SHA-256 e Outbox são commitados juntos; recusas determinísticas são registradas na Inbox sem produzir evento de domínio.

O cenário provider-neutral comprovou:

- duas partidas concorrentes com o mesmo comando produziram 1 aplicação e 1 replay, com uma única tentativa;
- uma partida com versão antiga retornou `VersionConflict`, não gerou ledger e repetiu deterministicamente pela Inbox;
- completion exigiu evidência, moveu tarefa/tentativa para `awaiting_review` e incrementou a versão;
- tarefa de risco médio recusou autoaprovação com `IndependentReviewerRequired`;
- o agente `critic-qa` aprovou, levando a tarefa a `completed` e a tentativa a `approved`;
- replays de completion/review preservaram os hashes originais e chave reutilizada com payload diferente lançou `IdempotencyConflictException`;
- o snapshot final apresentou versão 4, 1 tentativa, 1 evidência e 1 review, sem duplicação.

Evidência executada: SQLite focado 1/1 e PostgreSQL focado 1/1; `tools/backend/verify.sh` exit code 0; restore locked e format verdes; build Release 0 warnings/0 errors; suíte 74/74 (`Unit 46`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 12`). O fixture PostgreSQL realizou cleanup completo: zero containers, volumes ou networks com `com.harness.managed=true` após o teste.

Esta fatia não encerra EP-09b.2b: falta reidratar as coleções completas de instruções, tentativas, evidências e reviews e persistir uma nova versão de instrução após review rejeitado.
