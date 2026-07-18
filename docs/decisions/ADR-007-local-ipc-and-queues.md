# ADR-007 — IPC loopback autenticado e filas internas

- Status: aceito
- Data: 2026-07-18

## Decisão

Launcher cria porta dinâmica e token efêmero. Runner comunica heartbeat, checkpoint e resultado ao Host por HTTP somente em loopback, com `runnerId`, `attemptId`, `sequence` e `idempotencyKey`. `System.Threading.Channels` existe apenas dentro do Host.

Na PoC-9, contratos vivem no SharedKernel e o Runner usa somente `HttpClient`. O Host recusa endereço remoto não-loopback, compara o bearer token por hash SHA-256 em tempo constante e mantém Inbox/estado sob uma única autoridade transacional in-memory para isolar o protocolo. A Fase 1 substitui esse store pela Inbox e estado relacionais sem mudar o envelope. Replay idêntico retorna receipt `replay=true` sem aplicar novamente; reutilização conflitante da chave, sequência atrasada, lacuna, troca de owner ou mensagem posterior à conclusão retornam `application/problem+json`.

O token é passado ao Runner apenas por arquivo efêmero `0600`, nunca por argumento, ambiente, resposta ou log. O endpoint `/api/v1/internal/runner/messages` é excluído do OpenAPI público e o processo Runner valida que a URL é HTTP loopback antes de enviar.

## Consequências

O token nunca é logado. Mensagem repetida não duplica efeito e sequência fora de ordem é rejeitada com a sequência esperada para reconciliação pelo caller. O Runner não referencia nenhum provider de persistência. RabbitMQ fica fora do modo pessoal.
