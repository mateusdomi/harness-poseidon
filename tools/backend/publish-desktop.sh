#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly RID="${1:-osx-arm64}"
readonly OUTPUT="${REPOSITORY_ROOT}/.artifacts/desktop/${RID}"

cd "${REPOSITORY_ROOT}"

"${TOOLS_DIR}/build-frontend.sh"
rm -rf "${OUTPUT}"
"${DOTNET}" publish src/Harness.Launcher/Harness.Launcher.csproj \
  --configuration Release \
  --runtime "${RID}" \
  --self-contained true \
  -p:PublishSingleFile=false \
  --output "${OUTPUT}"
rsync -a --delete "${REPOSITORY_ROOT}/src/Harness.Host/wwwroot/" "${OUTPUT}/wwwroot/"

echo "Publicado em ${OUTPUT}"
echo "Execute: ${OUTPUT}/Harness.Launcher [--port <porta>] [--data-dir <caminho>] [--no-browser]"
