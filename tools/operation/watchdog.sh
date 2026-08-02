#!/usr/bin/env bash
# Watchdog da operação final (§5/§6 da ordem executiva).
#
# Classifica cada attempt em execução por SINAIS REAIS, nunca por timeout cego:
#   WORKING          PID vivo + (CPU ativa OU output/heartbeat recente)
#   WAITING_EXTERNAL PID vivo, sem CPU, mas com chamada de modelo em voo
#   STALLED          banco diz running, mas não há PID, nem CPU, nem delta
#   ORPHAN_DB        attempt running cujo host nem está de pé
#
# "WAITING" genérico é proibido: toda linha sai com um motivo observável.
set -uo pipefail

DB="${POSEIDON_DB:-$HOME/.harness-poseidon/harness.db}"
DATA_DIR="${POSEIDON_DATA_DIR:-$HOME/.harness-poseidon}"
STALL_SECONDS="${STALL_SECONDS:-600}"

q() { sqlite3 -noheader -separator '|' "$DB" "$1" 2>/dev/null; }

now_epoch=$(date -u +%s)

host_pid=""
if [[ -f "$DATA_DIR/launcher.pid" ]]; then
  candidate=$(cat "$DATA_DIR/launcher.pid" 2>/dev/null)
  if [[ -n "$candidate" ]] && kill -0 "$candidate" 2>/dev/null; then
    host_pid="$candidate"
  fi
fi

echo "=== WATCHDOG $(date -u +%Y-%m-%dT%H:%M:%SZ) ==="
if [[ -n "$host_pid" ]]; then
  echo "host: UP pid=$host_pid"
else
  echo "host: DOWN (nenhum processo vivo em $DATA_DIR/launcher.pid)"
fi

# Pressão de máquina — governa a liberação de operações HEAVY.
pages_free=$(vm_stat | awk '/Pages free/ {gsub("\\.","",$3); print $3}')
pages_spec=$(vm_stat | awk '/Pages speculative/ {gsub("\\.","",$3); print $3}')
free_mb=$(( (pages_free + pages_spec) * 16384 / 1048576 ))
swap_used=$(sysctl -n vm.swapusage | awk '{print $6}')
echo "host_mem: free=${free_mb}MB swap_used=${swap_used}"

echo
printf '%-10s %-34s %-16s %-12s %-8s %-9s %s\n' \
  ATTEMPT CARD ESTADO_BANCO IDADE PID CPU% CLASSIFICACAO

# Attempts que o banco considera em execução.
q "select a.id, substr(t.title,1,32), a.state, a.operational_state, a.started_at, a.producer_agent_id
   from work_attempts a join work_tasks t on t.id = a.task_id
   where a.operational_state in ('running','queued')
   order by a.started_at;" |
while IFS='|' read -r att title state opstate started agent; do
  [[ -z "$att" ]] && continue

  started_epoch=$(date -u -j -f "%Y-%m-%dT%H:%M:%S" "${started:0:19}" +%s 2>/dev/null || echo "$now_epoch")
  age=$(( now_epoch - started_epoch ))

  # Procura um processo real cujo comando cite este attempt ou seu workspace.
  pid=$(pgrep -f "$att" 2>/dev/null | head -1)
  cpu="-"
  klass=""

  if [[ -z "$pid" && -z "$host_pid" ]]; then
    klass="ORPHAN_DB"
  elif [[ -z "$pid" ]]; then
    # Sem processo próprio: só é legítimo se o host tiver trabalho em voo por ele.
    inflight=$(q "select count(*) from model_invocations
                  where attempt_id='$att' and completed_at is null;")
    if [[ "${inflight:-0}" -gt 0 ]]; then
      klass="WAITING_EXTERNAL(model_call)"
    elif [[ "$age" -gt "$STALL_SECONDS" ]]; then
      klass="STALLED(sem pid, sem chamada, ${age}s)"
    else
      klass="WORKING(host in-proc, ${age}s)"
    fi
  else
    cpu=$(ps -o %cpu= -p "$pid" 2>/dev/null | tr -d ' ')
    klass="WORKING(pid vivo)"
  fi

  printf '%-10s %-34s %-16s %-12s %-8s %-9s %s\n' \
    "${att:0:8}" "$title" "$opstate" "${age}s" "${pid:--}" "$cpu" "$klass"
done

echo
stalled=$(q "select count(*) from work_attempts where operational_state='running';")
echo "attempts em 'running' no banco: ${stalled:-0}"
echo "regra: running no banco sem PID, sem chamada e sem delta => investigar AGORA, não esperar."
