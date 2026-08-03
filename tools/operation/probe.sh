#!/usr/bin/env bash
# Sonda de observabilidade da Operação Final (R2).
#
# Acompanha UM SUJEITO concreto — turn, card ou attempt — e devolve o controle rápido.
# Nunca conta linhas ("existem 2 mensagens?"): essa pergunta não identifica nada, e foi ela
# que prendeu a Integradora por nove minutos enquanto a resposta esperada já existia havia
# vinte e quatro segundos.
#
#   probe.sh turn    <turn_id>
#   probe.sh card    <card_id>
#   probe.sh attempt <attempt_id>
#
# Bloqueia no MÁXIMO PROBE_BUDGET_SECONDS (padrão 25s, teto duro de 30s). Se o sujeito
# ainda não terminou, sai com 10 = "ainda em voo, volte depois" — e quem chamou fica livre
# para investigar outra coisa em vez de olhar para uma tela.
#
# Saídas: 0 fora de voo · 10 em voo · 20 STALLED · 2 uso inválido
# O veredito diz O QUE aconteceu: COMPLETED, FAILED, IDLE (voltou para a fila) ou SETTLED.
set -uo pipefail

DB="${POSEIDON_DB:-$HOME/.harness-poseidon/harness.db}"
BUDGET="${PROBE_BUDGET_SECONDS:-25}"
[[ "$BUDGET" -gt 30 ]] && BUDGET=30
INTERVAL="${PROBE_INTERVAL_SECONDS:-5}"
STALL_SECONDS="${STALL_SECONDS:-600}"

kind="${1:-}"; subject="${2:-}"
if [[ -z "$kind" || -z "$subject" ]]; then
  echo "uso: probe.sh <turn|card|attempt> <id>" >&2
  exit 2
fi

q() { sqlite3 -noheader -separator '|' "$DB" "$1" 2>/dev/null; }

# Estado terminal por tipo de sujeito. Cada consulta cita o ID — nunca agrega.
read_state() {
  case "$kind" in
    turn)
      q "select state from chief_turn_intents where turn_id='$subject'
         union all select state from chief_turn_blocks where turn_id='$subject' limit 1;"
      ;;
    card)    q "select state from work_tasks where id='$subject';" ;;
    attempt) q "select operational_state from work_attempts where id='$subject';" ;;
    *) echo "sujeito inválido: $kind" >&2; exit 2 ;;
  esac
}

is_terminal() {
  case "$1" in
    completed|done|merged|approved|failed|cancelled|rejected|escalated|blocked|ready) return 0 ;;
    *) return 1 ;;
  esac
}

# O VEREDITO precisa dizer o que aconteceu, e não apenas que a espera acabou.
#
# `ready` estava na mesma sacola de `completed` e a sonda respondia COMPLETED para um card
# que tinha voltado à fila depois de cinquenta tentativas fracassadas — a leitura mais
# perigosa possível, porque quem pergunta conclui que o trabalho saiu. Sair da espera não é
# ter terminado: um card em `ready` não está em voo, mas também não entregou nada.
verdict_for() {
  case "$1" in
    completed|done|merged|approved) echo "COMPLETED" ;;
    failed|cancelled|rejected|escalated|blocked) echo "FAILED" ;;
    ready) echo "IDLE" ;;
    *) echo "SETTLED" ;;
  esac
}

# Sinais reais de que algo ainda acontece — a diferença entre esperar e estar travado.
has_live_signal() {
  local inflight
  inflight=$(q "select count(*) from model_invocations
                where (attempt_id='$subject' or work_task_id='$subject')
                  and completed_at is null;")
  [[ "${inflight:-0}" -gt 0 ]] && return 0
  pgrep -f "$subject" >/dev/null 2>&1 && return 0
  return 1
}

started=$SECONDS
last=""
while (( SECONDS - started < BUDGET )); do
  last="$(read_state | head -1)"
  if [[ -n "$last" ]] && is_terminal "$last"; then
    echo "subject=$kind:$subject state=$last verdict=$(verdict_for "$last")"
    exit 0
  fi
  sleep "$INTERVAL"
done

# Estourou o orçamento: NÃO continuar esperando. Classificar e devolver o controle.
if has_live_signal; then
  echo "subject=$kind:$subject state=${last:-unknown} verdict=WAITING_OBSERVABLE signal=live"
  exit 10
fi

age=$(q "select cast((julianday('now') - julianday(coalesce(
            (select started_at from work_attempts where id='$subject'),
            (select updated_at from work_tasks where id='$subject'),
            'now'))) * 86400 as integer);")

if [[ "${age:-0}" -gt "$STALL_SECONDS" ]]; then
  echo "subject=$kind:$subject state=${last:-unknown} age=${age}s verdict=STALLED"
  echo "não espere: diagnostique (pid, chamada de modelo, heartbeat, worktree, log)." >&2
  exit 20
fi

echo "subject=$kind:$subject state=${last:-unknown} age=${age:-0}s verdict=WAITING_OBSERVABLE"
exit 10
