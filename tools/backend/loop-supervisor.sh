#!/usr/bin/env bash
# SUPERVISOR EXTERNO do loop do Chefe — mecanismo de recuperação que NÃO depende do processo que
# ele vigia. Só faz `curl` no /health e, se o loop estiver TRAVADO (HTTP 503) ou o host sumir por
# tempo demais, mata e relança o host. Determinístico: sem modelo, sem estado escondido.
#
# Pareado com o watchdog IN-PROCESS (ChiefLoopWatchdogService): aquele derruba o /health para 503
# quando o loop trava; este, de fora, age sobre o 503. Se o runtime inteiro travar (o in-process
# não roda), o "inacessível" também dispara o reinício. Duas camadas, uma garantia.
#
# Ordem do dono 2026-08-08: a fábrica nunca pode ficar parada em silêncio.
set -uo pipefail

HEALTH_URL="${POSEIDON_HEALTH_URL:-http://127.0.0.1:5173/health}"
DATA_DIR="${POSEIDON_DATA_DIR:-$HOME/.harness-poseidon}"
LAUNCHER="${POSEIDON_LAUNCHER:-$HOME/Documents/harness-poseidon-backend/src/Harness.Launcher/bin/Release/net10.0/Harness.Launcher}"
INTERVAL="${POSEIDON_SUPERVISOR_INTERVAL:-30}"      # segundos entre sondagens
FAIL_THRESHOLD="${POSEIDON_FAIL_THRESHOLD:-2}"      # sondagens ruins seguidas antes de reiniciar
POSEIDON_ENV="${POSEIDON_ENV:-$HOME/.harness/poseidon.env}"
LOG="$DATA_DIR/logs/supervisor.log"

mkdir -p "$DATA_DIR/logs"

log() { printf '%s %s\n' "$(date -u +%FT%TZ)" "$*" >> "$LOG"; }

notify() {
  # Aviso best-effort ao dono. Nunca falha o supervisor se o Telegram estiver fora.
  [ -f "$POSEIDON_ENV" ] || return 0
  local token chat
  token="$(grep -E '^export Harness__Channels__Telegram__BotToken=' "$POSEIDON_ENV" 2>/dev/null | head -1 | cut -d= -f2-)"
  chat="$(grep -E '^export Harness__Channels__Telegram__AttentionChatId=' "$POSEIDON_ENV" 2>/dev/null | head -1 | cut -d= -f2-)"
  [ -n "$token" ] && [ -n "$chat" ] || return 0
  curl -s -m 10 "https://api.telegram.org/bot${token}/sendMessage" \
    --data-urlencode "chat_id=${chat}" \
    --data-urlencode "text=$1" >/dev/null 2>&1 || true
}

restart_host() {
  log "RESTART: loop travado/inacessível — matando launcher e relançando."
  pkill -f "Harness.Launcher --data-dir" 2>/dev/null || true
  sleep 3
  nohup "$LAUNCHER" --data-dir "$DATA_DIR" --no-browser \
    >> "$DATA_DIR/logs/host.stdout.log" 2>&1 &
  log "RESTART: relançado (pid $!). Aguardando subida."
  notify "⚠️ Poseidon: loop do Chefe travado — o supervisor reiniciou o host automaticamente. Nenhuma ação sua é necessária."
  # Carência de subida: não conta como falha enquanto o host novo inicializa.
  sleep 60
}

log "Supervisor iniciado (health=$HEALTH_URL, intervalo=${INTERVAL}s, limiar=${FAIL_THRESHOLD})."
fails=0
while true; do
  code="$(curl -s -o /dev/null -m 8 -w '%{http_code}' "$HEALTH_URL" 2>/dev/null || echo 000)"
  case "$code" in
    200)
      if [ "$fails" -ne 0 ]; then log "OK: health 200 (zerando contador, estava em $fails)."; fi
      fails=0
      ;;
    503|000)
      fails=$((fails + 1))
      log "RUIM: health=$code (falha $fails/$FAIL_THRESHOLD)."
      if [ "$fails" -ge "$FAIL_THRESHOLD" ]; then
        restart_host
        fails=0
      fi
      ;;
    *)
      # Qualquer outro código (ex.: 200 degraded serializado, 4xx) não é travamento: só registra.
      log "INFO: health=$code (não é travamento; sem ação)."
      fails=0
      ;;
  esac
  sleep "$INTERVAL"
done
