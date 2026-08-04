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
#
# O FORMATO É CONTRATO: `[<timestamp>] ALERTA <texto>`, com a palavra logo após o colchete.
# Quem consome este log — um `grep`, um monitor, outro agente — precisa ancorar em
# `^\[[^]]+\] ALERTA `, e não na palavra solta. Aprendido na primeira hora de uso: um agente
# anotou no log a frase "o ALERTA de binário defasado já tem publicador" e o monitor que
# vigiava a palavra solta acordou com a própria anotação. Um vigia que se acorda sozinho gasta
# atenção sem entregar informação, e é assim que se ensina alguém a ignorá-lo.
alert() { echo "[$(stamp)] ALERTA $*" >> "$LOG"; }

# Segundos de vida de um pid. `etime` é independente de idioma; `lstart` não é, e já
# respondeu "em dia" em pt-BR exatamente quando não estava (OPS-056).
uptime_seconds() {
  local etime
  etime=$(ps -o etime= -p "$1" 2>/dev/null | tr -d ' ')
  [[ -z "$etime" ]] && { echo 0; return; }
  awk -F'[-:]' '{ if (NF==4) print (($1*24+$2)*60+$3)*60+$4; else if (NF==3) print (($1*60)+$2)*60+$3; else if (NF==2) print ($1*60)+$2; else print 0 }' <<< "$etime"
}

say "vigia de pe (pid $$) — projeto $PROJECT, intervalo ${INTERVAL}s, teto de ociosidade ${IDLE_ALERT}min"

# Memória entre voltas: só alerta na TRANSIÇÃO, senão o log vira uma parede de repetição e
# quem lê aprende a ignorá-lo — que é o modo mais caro de um vigia falhar.
#
# UM ESTADO POR TIPO DE ALERTA, e não um só para todos. Enquanto era uma variável única, dois
# alertas simultâneos — card impedido e binário defasado — disputavam a mesma memória e se
# realertavam em revezamento a cada volta: cada um "apagava" o registro do outro e voltava a
# parecer novidade. Medido em 04/08 entre 03:19 e 03:25, três repetições do mesmo par. Um vigia
# que repete alarme é um vigia que ninguém lê, e este é o terceiro defeito desta família aqui.
last_done=-1
declare -A fired
idle_since=$(date -u +%s)
down_strikes=0

while true; do
  now=$(date -u +%s)

  # 1. O Host está de pé? Sem ele, nada mais importa e nada mais é diagnosticável.
  http=$(curl -s -m 5 -o /dev/null -w "%{http_code}" "$API/health" 2>/dev/null)
  http="${http:-000}"
  if [[ "$http" != "200" ]]; then
    # PUBLICAR DERRUBA O HOST DE PROPÓSITO, por cerca de um minuto. Alertar nessa janela é
    # acusar como incidente exatamente o procedimento que corrige incidentes — e foi o que
    # aconteceu às 03:02Z. Duas condições evitam isso, e nenhuma delas cega o vigia: com
    # publicador armado a queda é esperada e vira estado; sem ele, ainda assim se exige a
    # SEGUNDA leitura falha, porque uma amostra isolada não distingue reinício de morte.
    down_strikes=$(( down_strikes + 1 ))
    if pgrep -f "publish-when-idle" >/dev/null 2>&1; then
      say "host fora do ar (http=$http) durante publicacao — esperado, nada a fazer"
    elif [[ "$down_strikes" -ge 2 && -z "${fired[host_down]:-}" ]]; then
      alert "HOST FORA DO AR (http=$http) em duas leituras seguidas e sem publicacao em curso. A esteira inteira está parada."
      fired[host_down]=1
    fi
    sleep "$INTERVAL"; continue
  fi
  down_strikes=0

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
    # Progresso real limpa a memória de TODOS os alertas: o quadro mudou, e o que estava dito
    # sobre o quadro anterior pode ter deixado de valer.
    fired=()
  fi
  idle_min=$(( (now - idle_since) / 60 ))

  say "fase=${phase:-?} concluidos=$done_now em_voo=$running impedidos=$blocked ocioso=${idle_min}min"

  # 3. Card impedido é trabalho parado que ninguém vai destravar sozinho.
  if [[ "$blocked" -gt 0 && "${fired[blocked]:-}" != "$blocked" ]]; then
    alert "$blocked card(s) IMPEDIDO(S) — precisa de decisão:"
    q "select '  - '||substr(title,1,70)||' :: '||coalesce(blocked_reason,'(sem motivo registrado)') from work_tasks where project_id='$PROJECT' and (state='escalated' or board_state='blocked');" >> "$LOG"
    # A memória guarda QUANTOS: um quinto card impedido é notícia nova, o mesmo quarto não é.
    fired[blocked]="$blocked"
  fi

  # 4. Esteira parada: nada em voo E nada concluindo. Uma das duas sozinha é normal —
  #    juntas, por tempo demais, significam que ninguém está trabalhando e ninguém avisou.
  if [[ "$running" -eq 0 && "$idle_min" -ge "$IDLE_ALERT" && -z "${fired[idle]:-}" ]]; then
    alert "ESTEIRA PARADA há ${idle_min}min: nenhuma tentativa em voo e nenhum card novo concluído."
    q "select '  ultimo desfecho: '||outcome||' em '||invoked_at||' ('||account_alias||')' from model_invocations where project_id='$PROJECT' order by invoked_at desc limit 1;" >> "$LOG"
    fired[idle]=1
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
      if [[ "${last_src:-0}" -gt "$started" ]]; then
        # DEFASADO NÃO É O MESMO QUE DESAMPARADO. Durante uma campanha de correção o binário
        # fica defasado o tempo todo — é o estado normal entre um commit e a próxima janela
        # ociosa —, e alertar a cada commit gasta atenção sem pedir nenhuma ação: em duas das
        # três primeiras vezes já havia publicador armado e a resposta certa era não fazer
        # nada. O que merece alerta é defasado E ninguém publicando; ou publicador armado
        # HÁ TEMPO DEMAIS, que é o caso em que ele travou e o silêncio enganaria.
        publisher_pid=$(pgrep -f "publish-when-idle" | head -1)
        head_src=$(cd "$ROOT" && git log -1 --format=%h -- src/)
        if [[ -z "$publisher_pid" ]]; then
          if [[ "${fired[stale_binary]:-}" != "$head_src" ]]; then
            alert "BINARIO DEFASADO E SEM PUBLICADOR — o processo subiu há $etime, há commit em src/ mais novo ($head_src) e ninguém está publicando. Armar tools/operation/publish-when-idle.sh."
            fired[stale_binary]="$head_src"
          fi
        else
          publisher_min=$(( $(uptime_seconds "$publisher_pid") / 60 ))
          if [[ "$publisher_min" -ge "${VIGIL_PUBLISHER_STUCK_MINUTES:-45}" ]]; then
            if [[ -z "${fired[publisher_stuck]:-}" ]]; then
              alert "PUBLICADOR TRAVADO — armado há ${publisher_min}min (pid $publisher_pid) e o binário segue defasado ($head_src). Ele espera janela sem tentativa em voo; se a esteira nunca fica ociosa, ninguém publica nunca."
              fired[publisher_stuck]=1
            fi
          else
            say "binario defasado ($head_src) — publicador de pe ha ${publisher_min}min (pid $publisher_pid); nada a fazer"
          fi
        fi
      fi
    fi
  fi

  sleep "$INTERVAL"
done
