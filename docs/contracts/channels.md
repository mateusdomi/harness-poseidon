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

Telegram e Teams seguem o mesmo contrato dos futuros adapters WhatsApp e e-mail.
Falha de correlação ou autenticação bloqueia processamento e produz auditoria sem
expor o conteúdo sensível.
