# Evidência F2-DOGFOOD-1c — reconciliação de turnos do Chief

Data: 2026-07-18. Commit funcional publicado: `56abba2` em `develop`.

`POST /conversations/{id}/turns` agora encerra sua responsabilidade depois de persistir Inbox, mensagem humana e mailbox, retornando 202. `ChiefTurnBackgroundService` adquire a pendência por projeto, recompõe digest, chama `IAgentExecutor`, valida a saída e conclui transacionalmente. Falhas ficam isoladas por turno: saída inválida termina sem retry; falhas operacionais voltam a pending até três tentativas, sem registrar conteúdo sensível no erro.

`AcquireNextAsync` também seleciona mailbox `processing` cujo lease expirou. O teste de recuperação criou um turno fora do worker, adquiriu owner `crashed-owner`/fencing 1 com lease de 1 ms, encerrou o dispatcher e reiniciou o Host. O worker encontrou o abandono, elevou fencing a 2, executou o Fake e produziu o evento final. A mailbox terminou com duas tentativas; uma chamada posterior usando o lease/token 1 foi recusada por `ChiefTurnConflictException`.

O cenário HTTP original continua comprovando retorno 202, sete eventos ordenados e restart. `tools/backend/verify.sh` passou com 270/270 testes frontend e 174/174 backend (`Unit 96`, `Integration 37`, `Contract 28`, `Recovery 4`, `Architecture 6`, `Concurrency 3`), build Release com zero warnings/erros. `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
