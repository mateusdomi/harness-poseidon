# Evidência F2-NOTIF-1 — notificações e settings

Data: 2026-07-18. Commit funcional publicado: `c51606f` em `develop`.

A migration `0022_notifications_settings` cria settings 1:1 com perfil e notificações tenant/profile-scoped. A criação do primeiro perfil grava settings padrão na mesma transação. Tema, idioma, habilitação, categorias silenciadas, diretório de trabalho e aceite explícito do modo inseguro são validados e persistidos sem ampliar o escopo da sessão local.

Notificações validam os enums e limites do contrato. Uma `groupKey` repetida enquanto unread preserva o ID, atualiza a ocorrência mais recente e incrementa `dedupeCount`; depois de read/mute, uma nova ocorrência pode abrir outro grupo. Os comandos read/mute aceitam lotes limitados e só alcançam IDs pertencentes ao perfil autenticado. Cada criação ou coalescência publica `notification.created` com o contrato completo no stream privado `profile:<id>`.

Criação/coalescência, read/mute e PATCH de settings registram ledger SHA-256 encadeado e `audit.eventAppended` na mesma transação. O cenário HTTP comprovou sessão obrigatória, defaults, patch parcial, três dispatches, duas linhas finais, auditoria das quatro ações, status em lote e recuperação integral após restart. OpenAPI e drift cobrem exatamente `Notification` e `Settings` existentes no frontend, sem alterá-lo.

O gate também revelou uma espera racy no teste preexistente do Chief; a asserção passou a aguardar especificamente `chief.tasksDrained`, sem mudança de produção. `tools/backend/verify.sh` passou com restore locked, format, build Release 0 warnings/0 errors e 154/154 testes (`Unit 91`, `Integration 28`, `Contract 22`, `Recovery 4`, `Architecture 6`, `Concurrency 3`). `frontend/**` e `docs/frontend/**` permaneceram intactos.
