#!/usr/bin/env bash
set -euo pipefail

# PLAT-06: (re)gera o espelho documental determinístico das definições canônicas de agente
# em docs/agents/<key>.yaml a partir da fonte única (CanonicalAgentDefinitions). Re-executável:
# dado o mesmo código-fonte, produz saída byte-idêntica. O drift test garante que os ficheiros
# comprometidos nunca divergem silenciosamente da fonte.

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly DOTNET="${TOOLS_DIR}/dotnet.sh"

cd "${REPOSITORY_ROOT}"

"${DOTNET}" run \
  --project tools/Harness.AgentDocs.Tool/Harness.AgentDocs.Tool.csproj \
  --configuration Release \
  -- "${1:-generate}" "${REPOSITORY_ROOT}"
