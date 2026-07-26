#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly CONFIGURATION="${CONFIGURATION:-Release}"

cd "${REPOSITORY_ROOT}"

for script in migrate-to-server.sh publish-desktop.sh publish-server.sh verify-resilience.sh verify-sast.sh verify-release-candidate.sh; do
  bash -n "${TOOLS_DIR}/${script}"
  [[ -x "${TOOLS_DIR}/${script}" ]] || {
    echo "verify-operations: ${script} não é executável." >&2
    exit 1
  }
done

bash -n "${REPOSITORY_ROOT}/poseidon"
node --check "${TOOLS_DIR}/verify-package-first-run.mjs"
[[ -x "${REPOSITORY_ROOT}/poseidon" ]] || {
  echo "verify-operations: comando poseidon não é executável." >&2
  exit 1
}

for document in INSTALLATION.md OPERATIONS.md INCIDENT_RUNBOOK.md HOMOLOGATION.md; do
  [[ -s "docs/backend/operations/${document}" ]] || {
    echo "verify-operations: documentação ausente: ${document}." >&2
    exit 1
  }
done

"${DOTNET}" restore Harness.sln --locked-mode
"${DOTNET}" build Harness.sln --configuration "${CONFIGURATION}" --no-restore
"${DOTNET}" test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --configuration "${CONFIGURATION}" --no-build --no-restore \
  --filter 'FullyQualifiedName~LauncherSmokeTests|FullyQualifiedName~DesktopLifecycleTests|FullyQualifiedName~PostgresServerModeHostTests|FullyQualifiedName~LocalOperationsApiTests'
"${TOOLS_DIR}/scan-secrets.sh"

echo "verify-operations: documentação e fluxos operacionais verdes."
