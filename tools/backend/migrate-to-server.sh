#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"
readonly SOURCE_DATABASE="${1:-}"

if [[ -z "${SOURCE_DATABASE}" || ! -f "${SOURCE_DATABASE}" ]]; then
  echo "Uso: tools/backend/migrate-to-server.sh <arquivo-sqlite>" >&2
  exit 2
fi

if [[ -z "${POSEIDON_POSTGRES_CONNECTION:-}" ]]; then
  echo "POSEIDON_POSTGRES_CONNECTION precisa ser injetada no ambiente." >&2
  exit 2
fi

cd "${REPOSITORY_ROOT}"
exec "${DOTNET}" run \
  --project tools/Harness.Persistence.Migration.Tool/Harness.Persistence.Migration.Tool.csproj \
  --configuration Release \
  -- \
  --sqlite "${SOURCE_DATABASE}" \
  --confirm
