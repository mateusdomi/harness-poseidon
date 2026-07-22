#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Briefing de BOOT do Chefe (orquestrador do Poseidon).
#
# Disparado pelo hook SessionStart do Claude Code. Emite JSON em stdout com
# `hookSpecificOutput.additionalContext` — o único canal que injeta texto no
# contexto do modelo. É assim que o Chefe assume a persona de orquestrador
# ATIVO no boot: se apresenta, resume onde paramos, dá o status vivo do backlog
# (lido do DB durável) e PERGUNTA se pode começar. Nunca liga o auto-dispatch
# sozinho (trava de segurança).
#
# Read-only, sem rede além de um probe local de 1s, tolerante a falha: se o DB
# não existir/estiver travado ainda emite o briefing textual. Nunca imprime
# segredo (só conta cards).
# ---------------------------------------------------------------------------
set -uo pipefail

DB="${POSEIDON_DATA_DIR:-${HOME}/.harness-poseidon}/harness.db"

count() {
  sqlite3 -readonly -cmd ".timeout 800" "${DB}" \
    "SELECT COUNT(*) FROM work_tasks WHERE archived_at IS NULL${1};" 2>/dev/null
}

backlog_line="(banco ainda não inicializado — suba o Host uma vez com ./poseidon start)"
if command -v sqlite3 >/dev/null 2>&1 && [[ -f "${DB}" ]]; then
  ready="$(count " AND state='ready'")"
  running="$(count " AND state='running'")"
  review="$(count " AND state='awaiting_review'")"
  done="$(count " AND state='completed'")"
  projects="$(sqlite3 -readonly -cmd ".timeout 800" "${DB}" "SELECT COUNT(*) FROM projects;" 2>/dev/null)"
  backlog_line="projetos=${projects:-?} | backlog(ready)=${ready:-?} · em execução(running)=${running:-?} · aguardando review=${review:-?} · concluídos=${done:-?}"
fi

host_line="Host de homologação: parado (suba com um duplo-clique em Poseidon.command ou ./poseidon start)."
if curl -sf -o /dev/null --max-time 1 "http://127.0.0.1:5090/health" 2>/dev/null; then
  host_line="Host de homologação: NO AR em http://127.0.0.1:5090 (health 200)."
fi

BODY="$(cat <<BRIEF
[BOOT DO CHEFE — persona de orquestrador ATIVA]

Você é o Agente Chefe (orquestrador) do Poseidon. NÃO espere o usuário recitar contexto: já no início desta sessão, de forma proativa e em português, você deve:
  1. Se apresentar em 1 linha como o Chefe/orquestrador.
  2. Dar um resumo curto de onde paramos (use suas memórias: [[project-status]], [[chief-loop-runbook]], [[solved-problems]], [[notification-strategy]]).
  3. Mostrar o STATUS do projeto (números abaixo, lidos agora do DB durável).
  4. Listar 1–3 próximos passos concretos.
  5. PERGUNTAR se pode começar — o auto-dispatch nasce DESLIGADO por segurança; você só liga após o "pode começar" do usuário.

Seu papel é QUERER entregar o projeto: se algo depende do usuário, peça aprovação ou notifique — nunca deixe o trabalho parado esperando passivamente.

STATUS AGORA:
  ${backlog_line}
  ${host_line}

Regras absolutas (nunca violar): só 'develop'; nunca force push; nunca merge em 'main'; nunca GitHub Release; nunca declarar GNG-3; preservar ~/Poseidon-RC3-Usuario; nunca persistir segredo (contas por alias; segredos só no Keychain).
BRIEF
)"

if command -v jq >/dev/null 2>&1; then
  jq -n --arg ctx "${BODY}" \
    '{hookSpecificOutput:{hookEventName:"SessionStart",additionalContext:$ctx}}'
elif command -v python3 >/dev/null 2>&1; then
  BODY="${BODY}" python3 -c 'import json,os;print(json.dumps({"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":os.environ["BODY"]}}))'
else
  # Fallback mínimo sem encoder: escapa aspas/barras e newlines à mão.
  esc="${BODY//\\/\\\\}"; esc="${esc//\"/\\\"}"; esc="${esc//$'\n'/\\n}"
  printf '{"hookSpecificOutput":{"hookEventName":"SessionStart","additionalContext":"%s"}}\n' "${esc}"
fi
exit 0
