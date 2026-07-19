#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly RID="${1:-osx-arm64}"
readonly OUTPUT="${REPOSITORY_ROOT}/.artifacts/server/${RID}"

cd "${REPOSITORY_ROOT}"

restore_canonical_locks() {
  local status=$?
  trap - EXIT
  if ! "${DOTNET}" restore Harness.sln --force-evaluate >/dev/null; then
    echo "Falha ao restaurar o estado canônico dos lockfiles após o publish." >&2
    status=1
  fi
  exit "${status}"
}
trap restore_canonical_locks EXIT

"${TOOLS_DIR}/build-frontend.sh"
rm -rf "${OUTPUT}"
"${DOTNET}" publish src/Harness.Host/Harness.Host.csproj \
  --configuration Release \
  --runtime "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "${OUTPUT}"
rsync -a --delete "${REPOSITORY_ROOT}/src/Harness.Host/wwwroot/" "${OUTPUT}/wwwroot/"

echo "Servidor publicado em ${OUTPUT}"
echo "Consulte docs/backend/operations/INSTALLATION.md antes de configurar PostgreSQL/OIDC."
