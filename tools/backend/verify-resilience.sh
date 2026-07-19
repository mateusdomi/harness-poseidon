#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly CONFIGURATION="${CONFIGURATION:-Release}"

cd "${REPOSITORY_ROOT}"

"${DOTNET}" restore Harness.sln --locked-mode
"${DOTNET}" build Harness.sln --configuration "${CONFIGURATION}" --no-restore
"${DOTNET}" test tests/Harness.IntegrationTests/Harness.IntegrationTests.csproj \
  --configuration "${CONFIGURATION}" --no-build --no-restore \
  --filter 'FullyQualifiedName~SqliteMigrationUpgradeTests|FullyQualifiedName~LocalOperationsApiTests|FullyQualifiedName~PostgresMultiuserLoadTests|FullyQualifiedName~SqliteToPostgresMigrationTests'
"${DOTNET}" test tests/Harness.RecoveryTests/Harness.RecoveryTests.csproj \
  --configuration "${CONFIGURATION}" --no-build --no-restore \
  --filter 'FullyQualifiedName~ProductionDurableExecutionRecoveryTests|FullyQualifiedName~IsolatedAttemptRecoveryTests'

if command -v docker >/dev/null 2>&1; then
  if [[ -n "$(docker ps -aq --filter 'label=com.harness.managed=true')" ]] ||
     [[ -n "$(docker volume ls -q --filter 'label=com.harness.managed=true')" ]] ||
     [[ -n "$(docker network ls -q --filter 'label=com.harness.managed=true')" ]]; then
    echo "verify-resilience: recursos Docker gerenciados ficaram órfãos." >&2
    exit 1
  fi
fi

echo "verify-resilience: matriz de resiliência verde e sem recursos Docker órfãos."
