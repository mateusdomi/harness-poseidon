#!/usr/bin/env bash
# Gera o SBOM (pacotes diretos e transitivos com versões resolvidas) a partir
# dos lock files do NuGet. Saída: docs/backend/security/sbom.json
set -euo pipefail
repository_root="$(cd "$(dirname "$0")/../.." && pwd)"
temporary_sbom="$(mktemp "${TMPDIR:-/tmp}/poseidon-sbom.XXXXXX")"
cleanup() { [[ ! -f "${temporary_sbom}" ]] || unlink "${temporary_sbom}"; }
trap cleanup EXIT
cd "${repository_root}"
./tools/backend/dotnet.sh list Harness.sln package --include-transitive --format json >"${temporary_sbom}"
sed "s#${repository_root}#.#g" "${temporary_sbom}" >docs/backend/security/sbom.json
echo "SBOM gravado em docs/backend/security/sbom.json"
