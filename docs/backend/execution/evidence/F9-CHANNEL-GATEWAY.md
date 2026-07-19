# Evidência F9-1 — gateway de canais com linking e dedupe de reentrega

Data: 2026-07-19.

O primeiro canal externo (terminal) entrou pela infraestrutura de gateway que o Telegram e o Teams reutilizarão:

- **Linking explícito de identidade**: `POST /api/v1/channels/links` vincula uma identidade externa (`kind` fechado em `terminal|telegram|teams` + identidade) a perfil, projeto e a uma conversa dedicada do canal; idempotente por identidade (revincular devolve o mesmo link, sem conversa órfã) e auditado com `channel.linked` (migration 0032, unicidade por tenant/kind/identidade).
- **Mensagens idempotentes por id externo**: `POST /links/{id}/messages` deriva o **turn id deterministicamente** do id externo da mensagem — a reentrega do provedor produz o mesmo turno e o Inbox durável do pipeline do Chief garante zero duplicação de mensagem, execução e resposta; o receipt expõe `deduplicated`.
- **Resposta no canal de origem**: `GET /links/{id}/messages` entrega o histórico do canal (autor `user`/`chief`) com cursor — é o que o cliente de terminal (ou o webhook de saída do Telegram) consome.

O teste de integração comprova por HTTP: link do terminal criado e re-link idempotente; mensagem com proposta de demanda processada pelo Chief com **resposta visível no canal**; **reentrega proposital do mesmo id externo → `deduplicated=true`, mesmo turn id e contagem de mensagens inalterada**; a demanda proposta via canal materializada na cadeia de trabalho; e `channel.linked` único na auditoria. Isso cumpre, no gateway, o critério de saída de F9 ("webhook reentregue de propósito e zero duplicação") — a ligação Telegram real (bot token, webhook público) fica para quando houver conta/credencial, exatamente como a missão prevê para dependências externas.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 218/218 (`Unit 121`, `Integration 55`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`); SQLite `32→0`; zero Docker órfão; OpenAPI republicado.
