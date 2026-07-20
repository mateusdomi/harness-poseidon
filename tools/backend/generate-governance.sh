#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"

cd "${REPOSITORY_ROOT}"

"${DOTNET}" run \
  --project tools/Harness.Governance.Tool/Harness.Governance.Tool.csproj \
  --configuration Release \
  --no-restore \
  -- generate "${REPOSITORY_ROOT}"
