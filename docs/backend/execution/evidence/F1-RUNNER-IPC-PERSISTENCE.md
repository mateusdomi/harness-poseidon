# Evidência F1 — IPC Runner persistido pela autoridade do Host

- Executado em: 2026-07-18T14:00:22Z
- Providers: SQLite dispatcher e PostgreSQL 18.4/Npgsql 10.0.3
- Resultado: verde

`IRunnerMessageStore` passou a ser a fronteira comum entre a application service do Host e as implementações de persistência. As migrations provider-specific criam tentativa Runner, checkpoints e Inbox/Outbox próprios. Cada mensagem válida atualiza sequência/versão e grava Inbox+Outbox na mesma transação. SQLite executa pelo single-writer dispatcher; PostgreSQL serializa cada tentativa com `pg_advisory_xact_lock`.

O mesmo teste comportamental foi executado nos dois providers e comprovou:

- aplicação única e replay idempotente;
- duas entregas concorrentes do mesmo checkpoint resultando em uma aplicação e um replay;
- chave reutilizada com mensagem diferente rejeitada;
- lacuna de sequência e owner divergente rejeitados sem mutação;
- tentativa concluída rejeitando mensagens novas;
- snapshot final com sequência 3, versão 3, um heartbeat, um checkpoint, três Inbox e três Outbox.

A PoC-9 foi repetida com o subprocesso real `Harness.Runner`: o primeiro Host persistiu heartbeat, checkpoint e conclusão em SQLite e foi encerrado; uma nova instância do Host abriu o mesmo banco e recebeu as três mensagens novamente. O resultado reportou três replays, enquanto sequência, versão, checkpoint e contagens Inbox/Outbox permaneceram inalterados. Gap e token inválido continuaram sem criar tentativa. O teste de arquitetura confirmou que `Harness.Runner` não referencia nenhum assembly de persistência.

Durante o primeiro ensaio, a comparação direta de dois records contendo `IReadOnlyList` falhou por identidade da lista, embora todos os valores impressos fossem iguais. A asserção foi corrigida para comparar explicitamente os campos e a sequência de checkpoints; nenhuma implementação foi alterada por essa falha de teste.

Gate final: `tools/backend/verify.sh` exit 0; format verde; build Release 0 warnings/0 errors; 52/52 testes verdes (`Unit 28`, `Architecture 6`, `Concurrency 3`, `Recovery 2`, `Contract 3`, `Integration 10`). Cleanup deixou zero recursos Docker com `com.harness.managed=true`; `frontend/**` e `docs/frontend/**` permaneceram intactos.
