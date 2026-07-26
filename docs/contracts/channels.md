# Contrato de canais

## Envelope normalizado

Cada mensagem contém canal, identificador externo, `conversation_id`, direção,
dedup key, sequência e timestamp. Payloads preservam a proveniência do adapter sem
transportar segredos de autenticação.

## Correlação

`ChannelLinkStore` relaciona identidades e threads externas a uma conversa única.
Mensagens repetidas são deduplicadas; mensagens fora de ordem são reconciliadas
pela sequência. Anti-spoofing valida remetente, assinatura e tenant antes da
entrega ao plano de controle.

## Entrada e saída

Adapters convertem entrada para o envelope comum. Somente o Output Gateway publica
saídas e somente em nome da Bruna. O Notification Router escolhe o último canal
ativo permitido, respeitando preferência, consentimento e disponibilidade.

Telegram, Teams, WhatsApp e e-mail seguem o mesmo contrato. Falha de correlação ou
autenticação bloqueia processamento e produz auditoria sem expor o conteúdo
sensível.

## Adapters

| Canal | Entrada | Anti-spoofing | Saída |
|---|---|---|---|
| Terminal | API do gateway | sessão de perfil local | leitura pela API |
| Telegram | polling de updates | token do bot por ambiente | Bot API |
| Teams | webhook de atividade | token portador em tempo fixo, `serviceUrl` em allowlist | Bot Framework |
| WhatsApp | webhook da Cloud API | HMAC-SHA256 do corpo bruto em tempo fixo; verify token no handshake | Graph API |
| E-mail | webhook do provedor de recebimento | token portador em tempo fixo; remetente precisa ter vínculo | relay SMTP |

Credenciais vêm apenas do ambiente ou de referência opaca de cofre. Canal sem
credencial completa nasce desligado e é no-op — nunca entrega presumida.

## Último canal ativo

Quando vários vínculos compartilham a mesma conversa, apenas um publica a saída: o
de entrada mais recente, registrada em `last_inbound_at` pelo adapter a cada
mensagem aceita. O desempate é determinístico — data de vínculo, depois
identificador — para que qualquer réplica eleja o mesmo canal sem coordenação.
Vínculo único na conversa é sempre o ativo.
