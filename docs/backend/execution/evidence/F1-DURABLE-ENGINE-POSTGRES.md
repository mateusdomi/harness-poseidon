# Evidência F1 — motor durável PostgreSQL

- Executado em: 2026-07-18T14:37:36Z
- Incremento: EP-05c.2
- Resultado: verde

`PostgresDurableExecutionEngine` implementa integralmente `IDurableExecutionEngine` usando as migrations próprias do provider. A aquisição concorrente usa `FOR UPDATE SKIP LOCKED`; mutações pertencentes a uma execução usam locks transacionais e bloqueio de linha. Inbox, estado, attempts, checkpoints, transições e Outbox são persistidos atomicamente, sem conceder autoridade de escrita ao Runner.

O teste do adapter SQLite foi extraído para `DurableExecutionEngineBehavior` e executado sem bifurcação funcional nos dois providers. O cenário comum comprovou:

- start aplicado, replay idempotente e conflito de fingerprint;
- dez aquisições concorrentes produzindo exatamente um lease, attempt 1 e fencing token 1;
- heartbeat, renovação, checkpoint, replay equivalente e rejeição de payload divergente;
- conclusão e rejeição de heartbeat atrasado;
- retry com backoff, attempt 2 com fencing crescente e dead-letter por reconciliação;
- timer, sinal e lifecycle `pause → resume → cancel` com controle otimista.

Durante a execução, duas falhas distintas foram diagnosticadas e corrigidas: uma transação tentava confirmar enquanto um reader vazio ainda estava aberto; depois, um fixture de validação de schema permanecia elegível e era corretamente adquirido pelo cenário comum. O reader passou a ser fechado antes do commit e o fixture passou a limpar sua execução. A representação normalizada de `jsonb` passou a ser validada por igualdade JSON estrutural, sem reduzir a verificação do conteúdo.

Evidência executada:

- teste PostgreSQL focado: 1/1 verde;
- comportamento dual-provider focado: 2/2 verde;
- `tools/backend/verify.sh`: exit code 0;
- build Release: 0 warnings, 0 errors;
- suíte completa: 62/62 verdes (`Unit 37`, `Architecture 6`, `Concurrency 3`, `Recovery 2`, `Contract 3`, `Integration 11`).

Esta fatia fecha a paridade de execução durável entre SQLite e PostgreSQL. GNG-2 continua fechado até a prova de encerramento abrupto sobre o motor de produção com auditoria completa.
