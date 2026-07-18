# Evidência F1 — schema dual-provider de workflows

- Executado em: 2026-07-18T15:44:37Z
- Incremento: EP-10b.1
- Resultado: verde

As migrations SQLite `0005_workflows.sql` e PostgreSQL `0006_workflows.sql` materializam dez tabelas conceituais: definição, versão, fase, item objetivo, gate e requisitos do gate; run, phase run, objective run e gate run.

Os providers compartilham invariantes, mas mantêm SQL próprio:

- IDs ULID, FKs compostas por tenant/projeto e versionamento otimista crescente;
- versão única por definição, fase/key/order únicos e item/key único por fase;
- tipos de item e estados fechados por `CHECK`;
- peso objetivo estritamente positivo;
- publicação coerente com `published_at`;
- gate e seus requisitos presos à mesma fase por FKs compostas;
- lifecycle temporal coerente para run, fase e gate;
- uma única fase `active` por run por índice parcial;
- projeções de objetivo/gate únicas por phase run.

O cenário de schema inseriu uma definição publicada, duas fases, objetivos ponderados, gate/requisito e run ativo com projeções. Uma segunda fase ativa no mesmo run foi rejeitada por unicidade nos dois bancos. Os runners comprovaram idempotência `SQLite 5→0` e `PostgreSQL 6→0`.

A suíte de recuperação de processo foi repetida após a nova migration e permaneceu 4/4. Gate integral: `tools/backend/verify.sh` exit code 0, restore locked/format verdes, build Release 0 warnings/0 errors e 83/83 testes (`Unit 55`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 12`). Cleanup final: zero containers, volumes ou networks com `com.harness.managed=true`.

Próximo incremento: EP-10b.2, store transacional provider-neutral para criar/publicar definição, iniciar/avançar run, Inbox/ledger/Outbox, optimistic concurrency, reidratação e progresso recalculado do banco.
