#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"

cd "${REPOSITORY_ROOT}"

[[ "$(git branch --show-current)" == "develop" ]] || {
  echo "verify-release-candidate: execute somente em develop." >&2
  exit 1
}

for document in \
  docs/backend/security/THREAT_MODEL.md \
  docs/backend/security/RELEASE_CHECKLIST.md \
  docs/backend/operations/INSTALLATION.md \
  docs/backend/operations/OPERATIONS.md \
  docs/backend/operations/INCIDENT_RUNBOOK.md \
  docs/backend/execution/ROADMAP_PROGRESS.md; do
  [[ -s "${document}" ]] || {
    echo "verify-release-candidate: documento obrigatório ausente: ${document}." >&2
    exit 1
  }
done

"${TOOLS_DIR}/verify.sh"
"${TOOLS_DIR}/verify-sast.sh"
"${TOOLS_DIR}/verify-resilience.sh"
"${TOOLS_DIR}/verify-operations.sh"
"${TOOLS_DIR}/sbom.sh"
"${TOOLS_DIR}/scan-secrets.sh"

for resource in \
  "$(docker ps -aq --filter label=com.harness.managed=true)" \
  "$(docker volume ls -q --filter label=com.harness.managed=true)" \
  "$(docker network ls -q --filter label=com.harness.managed=true)"; do
  [[ -z "${resource}" ]] || {
    echo "verify-release-candidate: recurso Docker Harness órfão: ${resource}." >&2
    exit 1
  }
done

[[ -z "$(git status --short -- frontend docs/frontend)" ]] || {
  echo "verify-release-candidate: área frontend protegida contém alterações locais." >&2
  exit 1
}

echo "verify-release-candidate: critérios técnicos automatizados verdes."
echo "GNG-6 permanece pendente até a11y/E2E real e aceites humanos registrados."
