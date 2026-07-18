#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly CONFIGURATION="${CONFIGURATION:-Release}"

cd "${REPOSITORY_ROOT}"

"${DOTNET}" restore Harness.sln --locked-mode
"${DOTNET}" format Harness.sln --verify-no-changes --no-restore
"${DOTNET}" build Harness.sln --configuration "${CONFIGURATION}" --no-restore
"${DOTNET}" test Harness.sln --configuration "${CONFIGURATION}" --no-build --no-restore
