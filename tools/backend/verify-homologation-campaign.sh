#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly CAMPAIGN_PATH="${1:-${REPOSITORY_ROOT}/docs/backend/execution/homologation-campaign.json}"

if [[ ! -f "${CAMPAIGN_PATH}" ]]; then
  if [[ $# -gt 0 ]]; then
    echo "verify-homologation-campaign: manifesto informado não encontrado." >&2
    exit 2
  fi

  bash -n "${TOOLS_DIR}/import-homologation-campaign.sh"
  echo "verify-homologation-campaign: campanha legada não configurada; OK"
  exit 0
fi

jq -e '
  .campaign.id == "HML-CAMPAIGN-2026-07-24" and
  .campaign.origin == "human_directed_maintenance" and
  (.campaign.title | type == "string" and length > 0) and
  (.demands | type == "array" and length > 0) and
  (.tasks | type == "array" and length > 0) and
  ([.demands[].key] | length == (unique | length)) and
  ([.tasks[].id] | length == (unique | length)) and
  (all(.demands[];
    (.key | test("^HML-P[0-3]$")) and
    (.title | type == "string" and length > 0) and
    (.priority | IN("low", "medium", "high", "critical")))) and
  (all(.tasks[];
    (.id | test("^HML-(COCKPIT|CHAT|PROJECTS|DELIVERY|WORKFLOW|AGENTS|DOCS|GOV|PROVIDERS|GLOBAL)-[A-Za-z0-9-]+$")) and
    (.demandKey as $key | any($root.demands[]; .key == $key)) and
    (.title | type == "string" and length > 0) and
    (.priority | IN("low", "medium", "high", "critical")) and
    (.cardType | IN("agent_task", "human_gate"))))
' --argjson root "$(jq '.' "${CAMPAIGN_PATH}")" "${CAMPAIGN_PATH}" >/dev/null

bash -n "${TOOLS_DIR}/import-homologation-campaign.sh"
echo "verify-homologation-campaign: OK"
