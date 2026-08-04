#!/usr/bin/env bash
# Notificação da Operação Final pelo Telegram.
#
# O token vem do Keychain; nunca de arquivo, argumento ou log. O chat de destino é
# DESCOBERTO em tempo de execução: um bot do Telegram não pode iniciar conversa, então só
# existe destino depois que o proprietário mandar qualquer mensagem para o bot uma vez.
#
#   notify.sh "texto"
#
# ATENÇÃO — este caminho NÃO é a Bruna. O `governance/core.md` diz que só ela publica para o
# usuário, e o Output Gateway do produto recusa qualquer outro autor (`DeniedNotChief`). Este
# script existe porque o alarme precisa sair MESMO com o Host fora do ar, que é justamente
# quando a Bruna não pode falar. O preço é que ele não passa pelas checagens dela — e em
# 03/08/2026 isso custou caro: o dono recebeu "nenhuma conta de ator disponível" quando as
# contas dele tinham cota e estavam apenas sendo desviadas para outro endpoint. Por isso toda
# mensagem daqui sai IDENTIFICADA como alarme automático: quem lê precisa saber que aquilo não
# foi a Bruna que disse, e que ninguém verificou.
#
# Saídas: 0 enviado · 10 sem destino (o dono ainda não falou com o bot) · 1 falha
set -uo pipefail

TEXT="${1:-}"
[[ -n "$TEXT" ]] || { echo "uso: notify.sh <texto>" >&2; exit 2; }

TOKEN="$(security find-generic-password -s "poseidon-telegram-bot" -w 2>/dev/null)"
[[ -n "$TOKEN" ]] || { echo "token do bot ausente no Keychain" >&2; exit 1; }

# O destino descoberto é LEMBRADO. O `getUpdates` do Telegram só devolve o que chegou nas
# últimas ~24 h: um destino descoberto ontem simplesmente some hoje, e o alarme emudece
# exatamente na noite em que ninguém está olhando o terminal. Foi o que aconteceu em
# 04/08/2026 às 03:45Z, com o OPS-018 dado como fechado desde 03/08.
#
# O id do chat não é segredo — é um destino, como um número de telefone; o token continua
# vindo só do Keychain e nunca toca o disco. O cache fica FORA do repositório, em ~/.harness,
# pela mesma razão de sempre: estado de instalação não é código.
CACHE="${POSEIDON_TELEGRAM_CHAT_CACHE:-$HOME/.harness/telegram-chat-id}"

CHAT="${POSEIDON_TELEGRAM_CHAT_ID:-}"
if [[ -z "$CHAT" ]]; then
  CHAT="$(curl -s --max-time 15 "https://api.telegram.org/bot${TOKEN}/getUpdates" |
    python3 -c "
import sys,json
try: d=json.load(sys.stdin)
except Exception: raise SystemExit
for u in reversed(d.get('result',[])):
    m=u.get('message') or u.get('edited_message') or {}
    c=(m.get('chat') or {}).get('id')
    if c: print(c); break" 2>/dev/null)"
fi

if [[ -n "$CHAT" ]]; then
  mkdir -p "$(dirname "$CACHE")" 2>/dev/null
  printf '%s\n' "$CHAT" > "$CACHE" 2>/dev/null
elif [[ -r "$CACHE" ]]; then
  CHAT="$(tr -d '[:space:]' < "$CACHE")"
fi

if [[ -z "$CHAT" ]]; then
  echo "sem destino: mande qualquer mensagem para @SystemPoseidon_bot uma vez e repita." >&2
  exit 10
fi

CODE="$(curl -s -o /tmp/poseidon-notify.out -w '%{http_code}' --max-time 20 \
  -X POST "https://api.telegram.org/bot${TOKEN}/sendMessage" \
  --data-urlencode "chat_id=${CHAT}" \
  --data-urlencode "text=[alarme automático da operação — não é a Bruna]
${TEXT}")"

if [[ "$CODE" == "200" ]]; then
  echo "notificado (chat ${CHAT})"
  exit 0
fi

echo "falha ao notificar: HTTP ${CODE}" >&2
head -c 300 /tmp/poseidon-notify.out >&2; echo >&2
exit 1
