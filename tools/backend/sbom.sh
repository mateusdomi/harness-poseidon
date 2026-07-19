#!/usr/bin/env bash
# Gera o SBOM (pacotes diretos e transitivos com versões resolvidas) a partir
# dos lock files do NuGet. Saída: docs/backend/security/sbom.json
set -euo pipefail
cd "$(dirname "$0")/../.."
./tools/backend/dotnet.sh list Harness.sln package --include-transitive --format json \
  > docs/backend/security/sbom.json
echo "SBOM gravado em docs/backend/security/sbom.json"
