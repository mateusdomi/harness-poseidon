#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DEFAULT_CAMPAIGN="${REPOSITORY_ROOT}/docs/backend/execution/homologation-campaign.json"

usage() {
  echo "uso: $0 --base-url http://127.0.0.1:PORT --cookie-jar PATH --project-id ULID [--campaign PATH]" >&2
}

base_url=""
cookie_jar=""
project_id=""
campaign_path="${DEFAULT_CAMPAIGN}"
while (($# > 0)); do
  case "$1" in
    --base-url) base_url="${2:-}"; shift 2 ;;
    --cookie-jar) cookie_jar="${2:-}"; shift 2 ;;
    --project-id) project_id="${2:-}"; shift 2 ;;
    --campaign) campaign_path="${2:-}"; shift 2 ;;
    *) usage; exit 2 ;;
  esac
done

if [[ ! "${base_url}" =~ ^http://(127\.0\.0\.1|localhost):[0-9]+$ ]]; then
  echo "import-homologation-campaign: somente Host HTTP loopback explícito é aceito." >&2
  exit 2
fi
if [[ ! "${project_id}" =~ ^[0-9A-HJKMNP-TV-Z]{26}$ ]]; then
  echo "import-homologation-campaign: project-id deve ser um ULID canônico." >&2
  exit 2
fi
if [[ ! -f "${cookie_jar}" || ! -r "${cookie_jar}" ]]; then
  echo "import-homologation-campaign: cookie-jar local legível é obrigatório." >&2
  exit 2
fi
if [[ ! -f "${campaign_path}" || ! -r "${campaign_path}" ]]; then
  echo "import-homologation-campaign: manifesto de campanha não encontrado." >&2
  exit 2
fi

"${TOOLS_DIR}/verify-homologation-campaign.sh" "${campaign_path}"

readonly WORK_DIR="$(mktemp -d)"
trap 'rm -rf "${WORK_DIR}"' EXIT

api_get() {
  curl --fail-with-body --silent --show-error \
    --cookie "${cookie_jar}" \
    "${base_url}$1"
}

api_post() {
  local path="$1"
  local payload="$2"
  curl --fail-with-body --silent --show-error \
    --cookie "${cookie_jar}" \
    --header "Content-Type: application/json" \
    --data "${payload}" \
    "${base_url}${path}"
}

origin="$(jq -r '.campaign.origin' "${campaign_path}")"
campaign_id="$(jq -r '.campaign.id' "${campaign_path}")"
campaign_title="${campaign_id} — $(jq -r '.campaign.title' "${campaign_path}")"

api_get "/api/v1/solicitations?projectId=${project_id}&limit=200" > "${WORK_DIR}/solicitations.json"
solicitation_id="$(jq -r --arg title "${campaign_title}" \
  '.items[] | select(.title == $title) | .id' "${WORK_DIR}/solicitations.json" | head -n 1)"
if [[ -z "${solicitation_id}" ]]; then
  payload="$(jq -nc \
    --arg projectId "${project_id}" \
    --arg kind "$(jq -r '.campaign.solicitationKind' "${campaign_path}")" \
    --arg title "${campaign_title}" \
    --arg body "origin=${origin}; $(jq -r '.campaign.body' "${campaign_path}")" \
    '{projectId:$projectId,kind:$kind,title:$title,body:$body}')"
  api_post "/api/v1/solicitations" "${payload}" > "${WORK_DIR}/solicitation-created.json"
  solicitation_id="$(jq -r '.id' "${WORK_DIR}/solicitation-created.json")"
  echo "CREATED solicitation ${campaign_title}"
else
  echo "EXISTS solicitation ${campaign_title}"
fi

while IFS= read -r demand; do
  key="$(jq -r '.key' <<<"${demand}")"
  title="${key} — $(jq -r '.title' <<<"${demand}")"
  api_get "/api/v1/demands?projectId=${project_id}&limit=200" > "${WORK_DIR}/demands.json"
  demand_id="$(jq -r --arg title "${title}" \
    '.items[] | select(.title == $title) | .id' "${WORK_DIR}/demands.json" | head -n 1)"
  if [[ -z "${demand_id}" ]]; then
    payload="$(jq -nc \
      --arg projectId "${project_id}" \
      --arg solicitationId "${solicitation_id}" \
      --arg title "${title}" \
      --arg description "origin=${origin}; campaign=${campaign_id}" \
      --arg priority "$(jq -r '.priority' <<<"${demand}")" \
      '{projectId:$projectId,solicitationId:$solicitationId,title:$title,description:$description,priority:$priority}')"
    api_post "/api/v1/demands" "${payload}" > "${WORK_DIR}/demand-created.json"
    echo "CREATED demand ${title}"
  else
    echo "EXISTS demand ${title}"
  fi
done < <(jq -c '.demands[]' "${campaign_path}")

while IFS= read -r task; do
  stable_id="$(jq -r '.id' <<<"${task}")"
  title="${stable_id} — $(jq -r '.title' <<<"${task}")"
  api_get "/api/v1/tasks?projectId=${project_id}&page=1&pageSize=200&archive=all" > "${WORK_DIR}/tasks.json"
  existing_id="$(jq -r --arg prefix "${stable_id} — " \
    '.items[] | select(.title | startswith($prefix)) | .id' "${WORK_DIR}/tasks.json" | head -n 1)"
  if [[ -n "${existing_id}" ]]; then
    echo "EXISTS task ${title}"
    continue
  fi

  demand_key="$(jq -r '.demandKey' <<<"${task}")"
  demand_title="${demand_key} — $(jq -r --arg key "${demand_key}" \
    '.demands[] | select(.key == $key) | .title' "${campaign_path}")"
  api_get "/api/v1/demands?projectId=${project_id}&limit=200" > "${WORK_DIR}/demands.json"
  demand_id="$(jq -r --arg title "${demand_title}" \
    '.items[] | select(.title == $title) | .id' "${WORK_DIR}/demands.json" | head -n 1)"
  if [[ -z "${demand_id}" ]]; then
    echo "import-homologation-campaign: demanda ${demand_title} não foi reconciliada." >&2
    exit 1
  fi

  instruction="origin=${origin}; campaign=${campaign_id}; stable_id=${stable_id}; verificar existência e classificar antes de editar; preservar o que funciona; implementar somente lacunas comprovadas; executar gates proporcionais; anexar commit e evidência; não declarar homologação ou GNG."
  payload="$(jq -nc \
    --arg projectId "${project_id}" \
    --arg demandId "${demand_id}" \
    --arg title "${title}" \
    --arg instruction "${instruction}" \
    --arg priority "$(jq -r '.priority' <<<"${task}")" \
    --arg cardType "$(jq -r '.cardType' <<<"${task}")" \
    '{projectId:$projectId,demandId:$demandId,title:$title,instruction:$instruction,priority:$priority,cardType:$cardType}')"
  api_post "/api/v1/tasks" "${payload}" > "${WORK_DIR}/task-created.json"
  echo "CREATED task ${title}"
done < <(jq -c '.tasks[]' "${campaign_path}")

api_get "/api/v1/tasks?projectId=${project_id}&page=1&pageSize=200&archive=all" > "${WORK_DIR}/tasks-final.json"
expected="$(jq '.tasks | length' "${campaign_path}")"
observed="$(jq --slurpfile campaign "${campaign_path}" \
  '[$campaign[0].tasks[].id as $id |
    select(any(.items[]; .title | startswith($id + " — ")))] | length' \
  "${WORK_DIR}/tasks-final.json")"
if [[ "${observed}" -lt "${expected}" ]]; then
  echo "import-homologation-campaign: reconciliação incompleta (${observed}/${expected})." >&2
  exit 1
fi
echo "OK campaign=${campaign_id} origin=${origin} tasks=${observed}/${expected}"
