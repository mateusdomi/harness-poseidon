# Evidência F1 — worker durável da Outbox

- Executado em: 2026-07-18T17:26:51Z
- Incremento: F1-WRK-1c
- Resultado: verde

`OutboxDispatcherBackgroundService` conecta o contrato de claims da Outbox a um `IOutboxMessageSink` tipado. O loop possui owner estável por instância, lease, batch, polling e política de retry configuráveis; libera claims expirados antes da aquisição, conclui somente com owner/fencing atuais e registra falhas pelo store. Cancelamento não é convertido em falha: a claim permanece cercada até expirar e outra instância a recupera com token maior.

Para não persistir dados potencialmente sensíveis lançados por adapters, o worker grava apenas o nome do tipo da exceção no histórico, nunca sua mensagem. Falhas terminais, backoff e dead-letter permanecem responsabilidade determinística de `IOutboxStore`.

Dois testes sobre SQLite/migrations reais passaram. No primeiro, duas mensagens foram adquiridas: o sink falhou uma vez e a outra foi concluída; snapshot intermediário `1 pending/0 claimed/1 dispatched/1 failure`. Uma nova instância, após o backoff, concluiu somente a mensagem pendente; snapshot final `0/0/2/0/1`, sem redispatch da já concluída. No segundo, o serviço foi cancelado dentro do sink; o snapshot preservou `0 pending/1 claimed`. Após avançar o relógio além do lease, uma nova instância liberou, readquiriu e concluiu exatamente a mensagem interrompida.

Execução focada: 2/2 testes verdes. Gate integral `tools/backend/verify.sh`: exit code 0, restore locked/format verdes, build Release com 0 warnings/0 errors e 96/96 testes (`Unit 65`, `Architecture 6`, `Concurrency 3`, `Recovery 4`, `Contract 3`, `Integration 15`). Inventário prévio preservou 11 containers de terceiros parados, 14 volumes e 7 networks; cleanup final confirmou zero recursos com `com.harness.managed=true`.

Próximo incremento: persistir os streams/eventos sequenciados em SQLite e PostgreSQL, trocar o snapshot em memória pelo store durável e implementar o sink Outbox→SignalR sem perder resync após restart.
