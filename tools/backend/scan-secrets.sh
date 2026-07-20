#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly SECRET_PATTERN='[0-9]{6,10}:AA[A-Za-z0-9_-]{30,}|-----BEGIN (RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----|AKIA[0-9A-Z]{16}|xox[baprs]-[0-9A-Za-z-]{10,}|AIza[0-9A-Za-z_-]{35}'
readonly COMMAND_SECRET_PATTERN='(^|[[:space:]])--?(token|api[-_]?key|secret|password)(=|[[:space:]])[^[:space:]]*'
readonly SCAN_MODE="${HARNESS_SECRET_SCAN_MODE:-full}"

[[ "${SCAN_MODE}" == "full" || "${SCAN_MODE}" == "tracked" ]] || {
  echo "scan-secrets: HARNESS_SECRET_SCAN_MODE deve ser full ou tracked." >&2
  exit 1
}

cd "${REPOSITORY_ROOT}"

declare -a findings=()

while IFS= read -r -d '' relative; do
  [[ -f "${relative}" ]] || continue
  case "${relative}" in
    *.lock.json|docs/backend/security/sbom.json|*.png|*.jpg|*.jpeg|*.gif|*.ico|*.webp|*.pdf|*.zip) continue ;;
  esac
  if LC_ALL=C grep -Iq . "${relative}" && LC_ALL=C grep -Eq "${SECRET_PATTERN}" "${relative}"; then
    findings+=("file:${relative}")
  fi
done < <(git ls-files -z --cached --others --exclude-standard)

if git diff --cached --no-ext-diff --unified=0 | LC_ALL=C grep -Eq "^\\+.*(${SECRET_PATTERN})"; then
  findings+=("staged-diff:high-signal-secret")
fi

if [[ "${SCAN_MODE}" == "full" && -d .artifacts ]]; then
  while IFS= read -r -d '' log_file; do
    if LC_ALL=C grep -Iq . "${log_file}" && LC_ALL=C grep -Eq "${SECRET_PATTERN}" "${log_file}"; then
      findings+=("log:${log_file}")
    fi
  done < <(find .artifacts -type f \( -name '*.log' -o -name '*.txt' -o -name '*.json' \) -print0)
fi

if [[ "${SCAN_MODE}" == "full" ]]; then
  while IFS= read -r process_line; do
    process_id="${process_line%% *}"
    process_command="${process_line#* }"
    if LC_ALL=C grep -Eq "${SECRET_PATTERN}|${COMMAND_SECRET_PATTERN}" <<<"${process_command}"; then
      findings+=("process:${process_id}:sensitive-command-argument")
    fi
  done < <(ps -axo pid=,command= | sed -E 's/^[[:space:]]+//')
fi

if (( ${#findings[@]} > 0 )); then
  echo "scan-secrets: falha; referências de alta precisão detectadas (valores omitidos):" >&2
  printf '  %s\n' "${findings[@]}" >&2
  exit 1
fi

echo "scan-secrets: modo ${SCAN_MODE}; superfícies habilitadas sem segredos de alta precisão."
