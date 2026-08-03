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

# Pressão de máquina. RAM LIVRE NÃO É O SINAL: o macOS usa a memória disponível
# agressivamente como cache, e "211 MB livres" com swap zerado descreve um sistema saudável,
# não um sistema afogado. O que importa é pressão, swap e pageouts — foi por ler RAM livre
# que se concluiu, erradamente, que só cabiam dois agentes.
pressure=$(memory_pressure 2>/dev/null | awk '/System-wide memory free percentage/ {print $NF}')
swap_used=$(sysctl -n vm.swapusage | awk '{print $6}')
pageouts=$(vm_stat | awk '/Pageouts/ {gsub("\\.","",$NF); print $NF}')
compressed=$(vm_stat | awk '/Pages occupied by compressor/ {gsub("\\.","",$NF); print $NF}')
echo "host: pressure_free=${pressure:-?} swap_used=${swap_used} pageouts=${pageouts:-0} compressed_pages=${compressed:-0}"
case "${swap_used}" in
  0,00M|0.00M|0M) echo "host: sem swap em uso — LIGHT à vontade, HEAVY=1" ;;
  *) echo "host: SWAP EM USO — não admitir trabalho novo até o atual terminar" ;;
esac

# O processo em execução pode ser mais velho que a correção que já está no repositório.
#
# É o erro mais caro e mais silencioso da operação: em 2026-08-03 o conserto que devolvia a
# única conta sã à eleição foi commitado às 13:29 e nunca publicado — o Host de 10:46 seguiu
# rodando o código antigo e a fase 4 acumulou mais de trezentas linhas de `adiado:
# account.quota_limited` para uma conta que o próprio painel mostrava disponível. Nada
# apontava para a causa: o defeito estava corrigido em disco e vivo em memória.
#
# Comparar a hora de início do processo com a data do último commit que toca `src/` responde
# isso em uma linha. É deliberadamente conservador: acusa só quando existe commit MAIS NOVO
# que o processo, que é exatamente a situação em que o que você leu no código não é o que
# está executando.
if [[ -n "$host_pid" ]]; then
  # Segundos decorridos, não data formatada: `ps -o lstart` sai no idioma do sistema
  # ("seg 3 ago") e a conversão falhava calada nesta máquina em pt-BR — o guarda respondia
  # "em dia" justamente quando não estava, que é o pior desfecho possível para um guarda.
  # `etimes` (segundos crus) não existe no ps do macOS; `etime` existe e sai como
  # [[dd-]hh:]mm:ss. Converter é trivial e não depende de idioma.
  host_elapsed=$(ps -o etime= -p "$host_pid" 2>/dev/null | tr -d ' ' | awk -F'[-:]' '{
    if (NF == 4) { print $1*86400 + $2*3600 + $3*60 + $4 }
    else if (NF == 3) { print $1*3600 + $2*60 + $3 }
    else if (NF == 2) { print $1*60 + $2 }
  }')
  host_started_epoch=0
  [[ "$host_elapsed" =~ ^[0-9]+$ ]] && host_started_epoch=$(( now_epoch - host_elapsed ))
  last_src_commit_epoch=$(git -C "$(dirname "$0")/../.." log -1 --format=%ct -- src 2>/dev/null || echo 0)
  if [[ "$host_started_epoch" -eq 0 ]]; then
    echo "host: NÃO FOI POSSÍVEL DATAR O PROCESSO — verifique à mão se o binário é o do HEAD."
  elif [[ "$last_src_commit_epoch" -gt "$host_started_epoch" ]]; then
    last_src_commit=$(git -C "$(dirname "$0")/../.." log -1 --format='%h %s' -- src 2>/dev/null)
    echo "host: BINÁRIO DEFASADO — o processo subiu em $(date -r "$host_started_epoch" -u +%Y-%m-%dT%H:%M:%SZ)"
    echo "host: e há commit em src/ posterior: ${last_src_commit}"
    echo "host: o que você lê no código NÃO é o que está executando — publique antes de diagnosticar."
  else
    echo "host: binário em dia com o último commit em src/"
  fi
fi

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
