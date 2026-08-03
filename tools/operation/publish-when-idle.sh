#!/usr/bin/env bash
# Publica o binário do Host na primeira janela sem tentativa em voo.
#
# Existe porque reiniciar o Host no meio de uma tentativa mata trabalho real — e porque
# NÃO reiniciar deixa o processo rodando código mais velho que o conserto, que foi o que
# custou uma hora da fase 4 em 2026-08-03 (OPS-056). As duas pressas se anulam: a saída é
# esperar a janela, e não decidir entre perder trabalho e ficar defasado.
#
# Ordem deliberada: pausar a esteira ANTES de conferir de novo. Pausar depois de conferir
# deixa a corrida aberta — o ciclo do chefe despacha em segundos, e já aconteceu de um
# despacho sair entre a leitura e a ação.
#
#   tools/operation/publish-when-idle.sh <project_id>
#
# Roda até publicar ou até PUBLISH_TIMEOUT_SECONDS (padrão 3600).
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DB="${POSEIDON_DB:-$HOME/.harness-poseidon/harness.db}"
API="${POSEIDON_API:-http://127.0.0.1:5173}"
PROJECT="${1:?uso: publish-when-idle.sh <project_id>}"
TIMEOUT="${PUBLISH_TIMEOUT_SECONDS:-3600}"
INTERVAL="${PUBLISH_INTERVAL_SECONDS:-10}"

log() { echo "[$(date -u +%H:%M:%SZ)] $*"; }

running_count() {
  sqlite3 -noheader "$DB" \
    "select count(*) from work_attempts where operational_state in ('running','queued');" 2>/dev/null
}

deadline=$(( $(date -u +%s) + TIMEOUT ))
while [[ $(date -u +%s) -lt "$deadline" ]]; do
  if [[ "$(running_count)" == "0" ]]; then
    log "janela aberta: nenhuma tentativa em voo — pausando a esteira"
    curl -sS -X POST "$API/api/v1/projects/$PROJECT/chief/pause" >/dev/null 2>&1

    # Confere DEPOIS de pausar: se um despacho escapou na fresta, devolve e volta a esperar.
    sleep 3
    if [[ "$(running_count)" != "0" ]]; then
      log "um despacho escapou na fresta — retomando e voltando a esperar"
      curl -sS -X POST "$API/api/v1/projects/$PROJECT/chief/resume" >/dev/null 2>&1
      sleep "$INTERVAL"
      continue
    fi

    log "publicando"
    "$ROOT/poseidon" stop  >/dev/null 2>&1
    if "$ROOT/poseidon" start >/dev/null 2>&1; then
      log "host reiniciado com o binário do HEAD ($(git -C "$ROOT" rev-parse --short HEAD))"
    else
      log "FALHA ao subir o Host — a esteira segue pausada de propósito, investigue antes de retomar"
      exit 1
    fi

    # Só retoma com o Host respondendo: retomar contra um Host morto registra um resume que
    # nunca aconteceu.
    for _ in $(seq 1 30); do
      curl -sf -m 3 "$API/health" >/dev/null 2>&1 && break
      sleep 2
    done
    curl -sS -X POST "$API/api/v1/projects/$PROJECT/chief/resume" >/dev/null 2>&1
    log "esteira retomada"
    exit 0
  fi
  sleep "$INTERVAL"
done

log "tempo esgotado sem janela — nada foi publicado, nada foi pausado"
exit 2
