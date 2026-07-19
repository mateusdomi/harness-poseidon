# Evidência F9-3 — smoke real do canal Telegram (@SystemPoseidon_bot)

Data: 2026-07-19.

Fecha a dependência externa que faltava na F9 (o smoke real dependia do token do BotFather).

## Setup

- Host real em `http://127.0.0.1:5090` (modo pessoal, SQLite persistente em
  `~/.harness-poseidon/homolog/harness.db`, 34 migrations aplicadas).
- Token do bot fornecido **apenas** via variável de ambiente
  `Harness__Channels__Telegram__BotToken` do processo Host. **Não** está em Git, banco, docs
  nem logs (o log registra só "Canal Telegram habilitado; polling de updates ativo"). O gate
  `SecretHygieneTests` (F11-3) garante que o token não seja commitado por engano.
- Bootstrap via API real: perfil → organização "Grupo S2" → projeto "Poseidon" → vínculo de canal.

## Vínculo

`POST /api/v1/channels/links` com `{ kind: "telegram", externalIdentity: "5774120296",
projectId }` criou o link e a conversa associada. A partir daí, mensagens do chat `5774120296`
deixam de receber "conversa não vinculada" e passam a materializar turnos do Chief na conversa
do projeto, com resposta entregue no próprio chat (mecanismo já coberto por `F9-TELEGRAM-ADAPTER`).

## Comprovação

- `getMe`: `ok=true`, `username=@SystemPoseidon_bot`, `id=8317959122` — token válido.
- Poller ativo no Host (long polling `getUpdates`), consumindo updates do bot.
- Notificação de saída entregue: `sendMessage` para o chat `5774120296` retornou HTTP 2xx
  (confirmação de vínculo + URL do Host). Entrega de saída ponta-a-ponta comprovada com o
  provedor real.

## Observações

- A URL `http://127.0.0.1:5090` é loopback: acessível apenas na máquina do usuário (onde o Host
  roda). Serve também para a homologação visual do GNG-3.
- O canal só responde enquanto o Host estiver no ar com o token no ambiente. Para religar:
  `export Harness__Channels__Telegram__BotToken="<token>"` e subir o Host/Launcher.
