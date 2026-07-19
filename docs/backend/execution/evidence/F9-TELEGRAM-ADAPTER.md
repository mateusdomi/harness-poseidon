# Evidência F9-2 — adaptador Telegram sobre o gateway de canais

Data: 2026-07-19.

`TelegramChannelBackgroundService` conecta o Telegram ao gateway de canais por **long polling** (`getUpdates`/`sendMessage`), sem webhook público — adequado ao modo pessoal:

- **Segurança do token**: o serviço só liga quando `Harness:Channels:Telegram:BotToken` está presente — fornecido **exclusivamente por variável de ambiente** (`Harness__Channels__Telegram__BotToken`). O token não aparece em repositório, documentação, banco ou logs (o log registra apenas "canal habilitado" e tipos de exceção; nenhuma URL é logada).
- **Linking explícito**: mensagens de chats não vinculados não criam turno — o bot responde orientando a vincular a identidade (chat id) na tela de canais; chats vinculados entram na conversa do link.
- **Dedupe de reentrega**: o turn id é derivado deterministicamente de `update_id` — a reentrega do provedor produz o mesmo turno e o Inbox durável garante zero duplicação (mesma garantia provada no gateway em F9-1).
- **Resposta no canal de origem**: as mensagens do Chief são entregues por `sendMessage` ao chat vinculado, com cursor por link e priming no boot que não reentrega histórico antigo (link novo entrega tudo a partir da criação — bug de primeira entrega detectado e corrigido durante a implementação).
- Falhas transitórias do provedor são isoladas sem derrubar o Host; `ApiBaseUrl` é configurável, o que permitiu o teste integral **sem rede externa** contra um servidor Telegram fake local (`HttpListener`).

O teste de integração comprova: priming sem reentrega; update real→turno→resposta do Chief entregue uma única vez no chat correto; **reentrega proposital do mesmo `update_id` → nenhuma nova resposta e conversa inalterada (2 mensagens)**; chat desconhecido recebe a orientação de linking com o próprio chat id.

Para ligar em produção: `export Harness__Channels__Telegram__BotToken="<token do BotFather>"` antes de subir o Host/Launcher, e vincule a identidade do chat (`/api/v1/channels/links`, kind `telegram`, externalIdentity = chat id numérico) — o bot informa o chat id na primeira mensagem não vinculada.

Gate: format sem mudanças; build Release zero warnings/erros; suíte integral 219/219 (`Unit 121`, `Integration 56`, `Contract 28`, `Recovery 5`, `Architecture 6`, `Concurrency 3`).
