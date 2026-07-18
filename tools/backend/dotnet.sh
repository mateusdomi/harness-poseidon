#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DOTNET_ROOT_LOCAL="${TOOLS_DIR}/.tooling/dotnet"
readonly DOTNET_BIN="${DOTNET_ROOT_LOCAL}/dotnet"

if [[ ! -x "${DOTNET_BIN}" ]]; then
  printf 'SDK local ausente. Execute tools/backend/install-dotnet.sh primeiro.\n' >&2
  exit 1
fi

export DOTNET_ROOT="${DOTNET_ROOT_LOCAL}"
exec "${DOTNET_BIN}" "$@"
