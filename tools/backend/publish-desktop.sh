#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly RID="${1:-osx-arm64}"
readonly OUTPUT="${REPOSITORY_ROOT}/.artifacts/desktop/${RID}"
readonly LAUNCHER_PROJECT="${REPOSITORY_ROOT}/src/Harness.Launcher/Harness.Launcher.csproj"
readonly LAUNCHER_DLL="${REPOSITORY_ROOT}/src/Harness.Launcher/bin/Release/net10.0/Harness.Launcher.dll"

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
"${DOTNET}" build "${LAUNCHER_PROJECT}" --configuration Release
"${DOTNET}" publish "${LAUNCHER_PROJECT}" \
  --configuration Release \
  --runtime "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "${OUTPUT}"
"${DOTNET}" publish src/Harness.Runner/Harness.Runner.csproj \
  --configuration Release \
  --runtime "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "${OUTPUT}/runner"
rsync -a --delete "${REPOSITORY_ROOT}/src/Harness.Host/wwwroot/" "${OUTPUT}/wwwroot/"

version="$(git rev-parse HEAD)"
if [[ -n "$(git status --porcelain)" ]]; then
  version="${version}-dirty"
fi
"${DOTNET}" "${LAUNCHER_DLL}" package-manifest \
  --package-dir "${OUTPUT}" \
  --rid "${RID}" \
  --version "${version}"

echo "Publicado em ${OUTPUT}"
echo "Instale: ${OUTPUT}/Harness.Launcher install --install-dir <destino> [--data-dir <dados>]"
