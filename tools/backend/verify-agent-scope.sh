#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"

cd "${REPOSITORY_ROOT}"

base_reference="${1:-${HARNESS_SCOPE_BASE:-}}"
if [[ -n "${base_reference}" ]]; then
  git cat-file -e "${base_reference}^{commit}" 2>/dev/null || {
    echo "verify-agent-scope: base de comparação inexistente." >&2
    exit 1
  }
  changed_paths="$(git diff --name-only "${base_reference}...HEAD")"
else
  changed_paths="$(git status --short | sed -E 's/^...//' | sed -E 's/.* -> //')"
fi

[[ -n "${changed_paths}" ]] || {
  echo "verify-agent-scope: nenhuma alteração; gate verde."
  exit 0
}

frontend_count=0
backend_count=0
while IFS= read -r path; do
  [[ -n "${path}" ]] || continue
  if [[ "${path}" == frontend/* || "${path}" == docs/frontend/* ]]; then
    frontend_count=$((frontend_count + 1))
  else
    backend_count=$((backend_count + 1))
  fi
done <<<"${changed_paths}"

if (( frontend_count > 0 && backend_count > 0 )); then
  echo "verify-agent-scope: alterações frontend e backend misturadas; claims independentes são obrigatórias." >&2
  exit 1
fi

if (( frontend_count > 0 )); then
  echo "verify-agent-scope: escopo Kimi validado (${frontend_count} paths protegidos)."
else
  echo "verify-agent-scope: escopo backend validado (${backend_count} paths; frontend intacto)."
fi
