#!/usr/bin/env bash
set -euo pipefail

readonly REQUIRED_SDK_VERSION="10.0.302"
readonly SCRIPT_SHA256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"
readonly SCRIPT_URL="https://dot.net/v1/dotnet-install.sh"
readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly TOOLING_DIR="${TOOLS_DIR}/.tooling"
readonly INSTALL_DIR="${TOOLING_DIR}/dotnet"
readonly INSTALL_SCRIPT="${TOOLING_DIR}/dotnet-install.sh"

if [[ -x "${INSTALL_DIR}/dotnet" ]]; then
  installed_version="$(${INSTALL_DIR}/dotnet --version)"
  if [[ "${installed_version}" == "${REQUIRED_SDK_VERSION}" ]]; then
    printf 'SDK .NET %s já instalado em %s\n' "${installed_version}" "${INSTALL_DIR}"
    exit 0
  fi

  printf 'Estado incompatível: SDK local %s encontrado; esperado %s.\n' \
    "${installed_version}" "${REQUIRED_SDK_VERSION}" >&2
  exit 1
fi

mkdir -p "${TOOLING_DIR}"
curl --fail --silent --show-error --location "${SCRIPT_URL}" --output "${INSTALL_SCRIPT}"

actual_hash="$(shasum -a 256 "${INSTALL_SCRIPT}" | awk '{print $1}')"
if [[ "${actual_hash}" != "${SCRIPT_SHA256}" ]]; then
  printf 'Checksum inesperado para dotnet-install.sh: %s\n' "${actual_hash}" >&2
  exit 1
fi

chmod 700 "${INSTALL_SCRIPT}"
"${INSTALL_SCRIPT}" \
  --version "${REQUIRED_SDK_VERSION}" \
  --install-dir "${INSTALL_DIR}" \
  --no-path

installed_version="$(${INSTALL_DIR}/dotnet --version)"
if [[ "${installed_version}" != "${REQUIRED_SDK_VERSION}" ]]; then
  printf 'Instalação inválida: SDK %s; esperado %s.\n' \
    "${installed_version}" "${REQUIRED_SDK_VERSION}" >&2
  exit 1
fi

printf 'SDK .NET %s instalado em %s\n' "${installed_version}" "${INSTALL_DIR}"
