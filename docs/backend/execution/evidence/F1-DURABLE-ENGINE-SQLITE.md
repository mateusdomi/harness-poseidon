# Evidência F1 — motor durável SQLite

- Executado em: 2026-07-18T14:24:12Z
- Incremento: EP-05c.1
- Resultado: verde

`SqliteDurableExecutionEngine` implementa integralmente `IDurableExecutionEngine` pelo dispatcher único. Start, lifecycle, lease/heartbeat, checkpoint, completion/failure, sinal, timer e reconciliação usam transações explícitas. Comandos externos persistem receipt na Inbox; mudanças de estado persistem histórico e Outbox na mesma transação. Checkpoint possui hash independente e chave estável; replay equivalente não grava novamente e payload divergente conflita.

O cenário executado comprovou:

- Start aplicado, replay da mesma chave e conflito da mesma chave com payload diferente;
- dez aquisições concorrentes produzindo exatamente um lease, attempt 1 e fencing token 1;
- heartbeat, renewal e checkpoint, incluindo replay por command key, replay por checkpoint key e conflito de payload;
- completion limpando owner ativo e rejeitando heartbeat atrasado pelo fencing;
- falha transitória indo a `WaitingForRetry`, timer promovendo a `Ready`, attempt 2 com fencing maior e reconciliação terminal em dead-letter após lease/heartbeat expirado;
- timer colocando execução em espera, sinal reativando e lifecycle `pause → resume → cancel` com versões otimistas.

O validator passou a exigir delays de retry em milissegundos inteiros, correspondendo ao schema sem truncamento silencioso. Gate: teste focado 1/1; `tools/backend/verify.sh` exit 0; build Release 0 warnings/0 errors; 62/62 testes verdes (`Unit 37`, `Architecture 6`, `Concurrency 3`, `Recovery 2`, `Contract 3`, `Integration 11`). Esta evidência não declara paridade PostgreSQL nem GNG-2 concluídos.
