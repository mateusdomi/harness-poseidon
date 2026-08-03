#!/usr/bin/env bash
# Vigia CONTÍNUO da operação. Roda sozinho, sem agente, sem cota.
#
# Por que existe: watchdog.sh e probe.sh classificam UM instante e saem. Alguém precisa
# chamá-los de novo, e até agora esse alguém era uma sessão de agente — que morre quando a
# sessão morre. Foi o que aconteceu em 2026-08-03 (OPS-056): a sessão anterior armou um vigia
# encarregado de publicar o binário, o vigia morreu junto com ela, e o Host passou horas
# rodando código mais velho que o conserto. Custou 310 linhas de log errado.
#
# A divisão de trabalho que este arquivo impõe:
#   - o LAÇO RÁPIDO é determinístico e gratuito (shell + sqlite), e roda sempre;
#   - o JULGAMENTO é caro (agente) e só deve acordar quando há o que julgar.
# Por isso a saída tem duas camadas: a linha de estado, que é ruído esperado, e a linha
# ALERTA, que é o único sinal que merece acordar alguém.
#
#   tools/operation/vigil.sh [projeto_id]
#
# Variáveis: VIGIL_INTERVAL_SECONDS (padrão 120), VIGIL_IDLE_ALERT_MINUTES (padrão 25),
#            VIGIL_LOG (padrão ~/.harness-poseidon/logs/vigil.log)
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DB="${POSEIDON_DB:-$HOME/.harness-poseidon/harness.db}"
DATA_DIR="${POSEIDON_DATA_DIR:-$HOME/.harness-poseidon}"
API="${POSEIDON_API:-http://127.0.0.1:5173}"
PROJECT="${1:-01KZ24JCFRHN2RGP8NHGP75JMK}"
INTERVAL="${VIGIL_INTERVAL_SECONDS:-120}"
IDLE_ALERT="${VIGIL_IDLE_ALERT_MINUTES:-25}"
LOG="${VIGIL_LOG:-$DATA_DIR/logs/vigil.log}"

mkdir -p "$(dirname "$LOG")"
q() { sqlite3 -readonly -noheader -separator '|' "$DB" "$1" 2>/dev/null; }
stamp() { date -u +%Y-%m-%dT%H:%M:%SZ; }
say()   { echo "[$(stamp)] $*" >> "$LOG"; }
# ALERTA é a única linha que pede julgamento humano ou de agente. Tudo o mais é estado.
alert() { echo "[$(stamp)] ALERTA $*" >> "$LOG"; }

say "vigia de pe (pid $$) — projeto $PROJECT, intervalo ${INTERVAL}s, teto de ociosidade ${IDLE_ALERT}min"

# Memória entre voltas: só alerta na TRANSIÇÃO, senão o log vira uma parede de repetição e
# quem lê aprende a ignorá-lo — que é o modo mais caro de um vigia falhar.
last_done=-1
last_alert_key=""
idle_since=$(date -u +%s)

while true; do
  now=$(date -u +%s)

  # 1. O Host está de pé? Sem ele, nada mais importa e nada mais é diagnosticável.
  http=$(curl -s -m 5 -o /dev/null -w "%{http_code}" "$API/health" 2>/dev/null || echo "000")
  if [[ "$http" != "200" ]]; then
    if [[ "$last_alert_key" != "host_down" ]]; then
      alert "HOST FORA DO AR (http=$http). A esteira inteira está parada."
      last_alert_key="host_down"
    fi
    sleep "$INTERVAL"; continue
  fi

  # 2. Progresso REAL: cards concluídos. É o único número que não mente sobre andar.
  done_now=$(q "select count(*) from work_tasks where project_id='$PROJECT' and board_state='done';")
  done_now="${done_now:-0}"
  running=$(q "select count(*) from work_attempts wa join work_tasks t on t.id=wa.task_id where t.project_id='$PROJECT' and wa.operational_state in ('running','queued');")
  running="${running:-0}"
  blocked=$(q "select count(*) from work_tasks where project_id='$PROJECT' and (state='escalated' or board_state='blocked');")
  blocked="${blocked:-0}"
  phase=$(q "select pd.name from workflow_phase_runs pr join workflow_phase_definitions pd on pd.id=pr.phase_definition_id where pr.project_id='$PROJECT' and pr.state='active' limit 1;")

  if [[ "$done_now" != "$last_done" ]]; then
    idle_since=$now
    last_done="$done_now"
    last_alert_key=""
  fi
  idle_min=$(( (now - idle_since) / 60 ))

  say "fase=${phase:-?} concluidos=$done_now em_voo=$running impedidos=$blocked ocioso=${idle_min}min"

  # 3. Card impedido é trabalho parado que ninguém vai destravar sozinho.
  if [[ "$blocked" -gt 0 && "$last_alert_key" != "blocked" ]]; then
    alert "$blocked card(s) IMPEDIDO(S) — precisa de decisão:"
    q "select '  - '||substr(title,1,70)||' :: '||coalesce(blocked_reason,'(sem motivo registrado)') from work_tasks where project_id='$PROJECT' and (state='escalated' or board_state='blocked');" >> "$LOG"
    last_alert_key="blocked"
  fi

  # 4. Esteira parada: nada em voo E nada concluindo. Uma das duas sozinha é normal —
  #    juntas, por tempo demais, significam que ninguém está trabalhando e ninguém avisou.
  if [[ "$running" -eq 0 && "$idle_min" -ge "$IDLE_ALERT" && "$last_alert_key" != "idle" ]]; then
    alert "ESTEIRA PARADA há ${idle_min}min: nenhuma tentativa em voo e nenhum card novo concluído."
    q "select '  ultimo desfecho: '||outcome||' em '||invoked_at||' ('||account_alias||')' from model_invocations where project_id='$PROJECT' order by invoked_at desc limit 1;" >> "$LOG"
    last_alert_key="idle"
  fi

  # 5. Binário defasado — o defeito que este arquivo existe para não repetir.
  if [[ -f "$DATA_DIR/launcher.pid" ]]; then
    host_pid=$(cat "$DATA_DIR/launcher.pid" 2>/dev/null)
    if [[ -n "$host_pid" ]] && kill -0 "$host_pid" 2>/dev/null; then
      # etime é independente de idioma; lstart não é (já falhou calado em pt-BR).
      etime=$(ps -o etime= -p "$host_pid" 2>/dev/null | tr -d ' ')
      up_s=$(awk -F'[-:]' '{ if (NF==4) print (($1*24+$2)*60+$3)*60+$4; else if (NF==3) print (($1*60)+$2)*60+$3; else if (NF==2) print ($1*60)+$2; else print 0 }' <<< "$etime")
      started=$(( now - ${up_s:-0} ))
      last_src=$(cd "$ROOT" && git log -1 --format=%ct -- src/ 2>/dev/null || echo 0)
      if [[ "${last_src:-0}" -gt "$started" && "$last_alert_key" != "stale_binary" ]]; then
        alert "BINARIO DEFASADO — o processo subiu há $etime e há commit em src/ mais novo ($(cd "$ROOT" && git log -1 --format=%h -- src/)). Publicar antes de diagnosticar qualquer coisa."
        last_alert_key="stale_binary"
      fi
    fi
  fi

  sleep "$INTERVAL"
done
