# Evidência F2-DOGFOOD-1b — mailbox e lease do Chief

Data: 2026-07-18. Commit funcional publicado: `f1d09f6` em `develop`.

A migration SQLite `0027_chief_turn_pipeline` criou `chief_states`, uma autoridade por `(tenant, projeto)`, e `chief_turn_mailbox`, isolada por tenant e ligada ao projeto, conversa e mensagens. O endpoint de turno agora persiste primeiro a mensagem humana, Inbox idempotente, mailbox `pending`, ledger e Outbox. Só depois adquire lease exclusivo, eleva fencing, marca mailbox/agente `processing`/`working`, recompõe o digest e chama `IAgentExecutor` fora da transação.

A conclusão valida atomicamente tenant, projeto, owner, fencing, expiração e mailbox processing. Em sucesso, grava a resposta do Chief e `chat_turns`, atualiza a conversa, produz chunks/evento final, persiste session ID e último digest, limpa o lease e volta o agente a idle. Saída estruturada inválida fecha a mailbox como erro e não fabrica resposta. O token antigo não possui caminho para concluir porque a verificação consulta o fencing corrente.

O cenário HTTP preservou o contrato público de sete eventos em sequência, comprovou mailbox `completed`, `attempt_count=1`, Inbox única, response vinculada, sessão Fake, digest presente, Chief idle, lease nulo e fencing 1. Após encerrar e reiniciar o Host sobre o mesmo SQLite, mensagens e todo o estado do pipeline permaneceram. A migration elevou o histórico idempotente a `27→0`; os testes de fundação e recovery foram atualizados para a nova contagem.

`tools/backend/verify.sh` passou com 270/270 testes frontend e 173/173 backend (`Unit 96`, `Integration 36`, `Contract 28`, `Recovery 4`, `Architecture 6`, `Concurrency 3`), build Release com zero warnings/erros. `frontend/**` e `docs/frontend/**` permaneceram sem edição backend.
