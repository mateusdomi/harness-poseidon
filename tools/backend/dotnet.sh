#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly DOTNET_ROOT_LOCAL="${TOOLS_DIR}/.tooling/dotnet"
readonly DOTNET_BIN="${DOTNET_ROOT_LOCAL}/dotnet"
readonly CLI_HOME="${TOOLS_DIR}/.tooling/cli-home"
readonly NUGET_PACKAGES_LOCAL="${TOOLS_DIR}/.tooling/nuget/packages"

if [[ ! -x "${DOTNET_BIN}" ]]; then
  printf 'SDK local ausente. Execute tools/backend/install-dotnet.sh primeiro.\n' >&2
  exit 1
fi

export DOTNET_ROOT="${DOTNET_ROOT_LOCAL}"
export DOTNET_CLI_HOME="${CLI_HOME}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
export DOTNET_MULTILEVEL_LOOKUP=0
export NUGET_PACKAGES="${NUGET_PACKAGES_LOCAL}"
exec "${DOTNET_BIN}" "$@"
