#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly RID="${POSEIDON_RELEASE_RID:-osx-arm64}"
readonly SHA="$(git -C "${REPOSITORY_ROOT}" rev-parse HEAD)"
readonly SHORT_SHA="${SHA:0:12}"
readonly RELEASE_ROOT="${REPOSITORY_ROOT}/.artifacts/release-candidate/${SHORT_SHA}-${RID}"
readonly PACKAGE_NAME="poseidon-${SHORT_SHA}-${RID}"
readonly PACKAGE_DIR="${RELEASE_ROOT}/${PACKAGE_NAME}"
readonly ARCHIVE="${RELEASE_ROOT}/${PACKAGE_NAME}.tar.gz"
readonly GATE_LOG="${RELEASE_ROOT}/gates.log"
readonly TEST_REPORT="${RELEASE_ROOT}/TEST_REPORT.md"
readonly SMOKE_ROOT="${RELEASE_ROOT}/.smoke"
readonly FRONTEND_WORK="${RELEASE_ROOT}/.frontend"

cd "${REPOSITORY_ROOT}"

fail() {
  echo "release-candidate: $*" >&2
  exit 1
}

[[ "$(git branch --show-current)" == "develop" ]] || fail "execute somente em develop."
[[ -z "$(git status --porcelain)" ]] || fail "working tree deve estar limpa antes do gate."
[[ "$(uname -s)" == "Darwin" ]] || fail "o RC desktop atual exige macOS."
[[ "${RID}" == osx-* ]] || fail "RID desktop inválido para este host: ${RID}."

for document in \
  docs/backend/security/THREAT_MODEL.md \
  docs/backend/security/RELEASE_CHECKLIST.md \
  docs/backend/operations/INSTALLATION.md \
  docs/backend/operations/OPERATIONS.md \
  docs/backend/operations/INCIDENT_RUNBOOK.md \
  docs/backend/operations/HOMOLOGATION.md \
  docs/backend/release/RELEASE_NOTES.md \
  docs/backend/execution/ROADMAP_PROGRESS.md; do
  [[ -s "${document}" ]] || fail "documento obrigatório ausente: ${document}."
done

case "${RELEASE_ROOT}" in
  "${REPOSITORY_ROOT}/.artifacts/release-candidate/"*) rm -rf "${RELEASE_ROOT}" ;;
  *) fail "diretório de saída inesperado." ;;
esac
mkdir -p "${RELEASE_ROOT}"
touch "${GATE_LOG}"

cleanup() {
  if [[ -x "${SMOKE_ROOT}/install/poseidon" ]]; then
    POSEIDON_DATA_DIR="${SMOKE_ROOT}/data" "${SMOKE_ROOT}/install/poseidon" stop >/dev/null 2>&1 || true
    POSEIDON_DATA_DIR="${SMOKE_ROOT}/demo-data" "${SMOKE_ROOT}/install/poseidon" stop >/dev/null 2>&1 || true
  fi
  case "${SMOKE_ROOT}" in "${RELEASE_ROOT}/.smoke") rm -rf "${SMOKE_ROOT}" ;; esac
  case "${FRONTEND_WORK}" in "${RELEASE_ROOT}/.frontend") rm -rf "${FRONTEND_WORK}" ;; esac
}
trap cleanup EXIT

run_gate() {
  local label="$1"
  shift
  echo "== ${label} ==" | tee -a "${GATE_LOG}"
  "$@" 2>&1 | tee -a "${GATE_LOG}"
}

storybook_gate() {
  env -u FORCE_COLOR NODE_NO_WARNINGS=1 npm run build-storybook -- --disable-telemetry 2>&1 | awk '
    /node_modules\/@storybook\/core\/dist\/preview\/runtime\.js .*Use of eval/ { next }
    /^\(!\) Some chunks are larger than 500 kB after minification/ { known_chunk = 4; next }
    known_chunk > 0 { known_chunk--; next }
    /[Ww]arning|\(!\)/ { print > "/dev/stderr"; unexpected = 1; next }
    { print }
    END { if (unexpected) exit 3 }
  '
}

frontend_browser_gate() {
  env -u FORCE_COLOR NODE_NO_WARNINGS=1 "$@" 2>&1 | awk '
    /node_modules\/@microsoft\/signalr\/dist\/esm\/Utils\.js .*: A comment/ { known_signalr = 4; next }
    known_signalr > 0 { known_signalr--; next }
    /[Ww]arning|\(!\)/ { print > "/dev/stderr"; unexpected = 1; next }
    { print }
    END { if (unexpected) exit 3 }
  '
}

run_gate "verify integral" "${TOOLS_DIR}/verify.sh"
run_gate "governance" "${TOOLS_DIR}/verify-governance.sh"
run_gate "SAST" "${TOOLS_DIR}/verify-sast.sh"
run_gate "resiliência" "${TOOLS_DIR}/verify-resilience.sh"
run_gate "operações" "${TOOLS_DIR}/verify-operations.sh"
run_gate "SBOM" "${TOOLS_DIR}/sbom.sh"
run_gate "segredos" "${TOOLS_DIR}/scan-secrets.sh"

[[ -z "$(git status --porcelain)" ]] || fail "um gate alterou arquivos versionados; revise e faça commit antes de repetir."

rsync -a --exclude node_modules --exclude dist "${REPOSITORY_ROOT}/frontend/" "${FRONTEND_WORK}/"
(
  cd "${FRONTEND_WORK}"
  run_gate "frontend npm ci" npm ci --no-audit --loglevel=error
  run_gate "frontend E2E" frontend_browser_gate npm run test:e2e
  run_gate "frontend a11y" frontend_browser_gate npm run test:a11y
  run_gate "frontend Storybook" storybook_gate
)

run_gate "publish self-contained" "${TOOLS_DIR}/publish-desktop.sh" "${RID}"
rsync -a "${REPOSITORY_ROOT}/.artifacts/desktop/${RID}/" "${PACKAGE_DIR}/"

mkdir -p "${SMOKE_ROOT}/install" "${SMOKE_ROOT}/data"
rmdir "${SMOKE_ROOT}/install"
run_gate "instalação limpa" "${PACKAGE_DIR}/Harness.Launcher" install \
  --install-dir "${SMOKE_ROOT}/install" --data-dir "${SMOKE_ROOT}/data"

export POSEIDON_DATA_DIR="${SMOKE_ROOT}/data"
run_gate "package start" "${SMOKE_ROOT}/install/poseidon" start --no-browser --port 5090
run_gate "package status" "${SMOKE_ROOT}/install/poseidon" status
mkdir -p "${RELEASE_ROOT}/screenshots"
(
  cd "${FRONTEND_WORK}"
  POSEIDON_BACKEND_URL=http://127.0.0.1:5090 \
    POSEIDON_EVIDENCE_DIR="${RELEASE_ROOT}/screenshots" \
    run_gate "frontend E2E API real" frontend_browser_gate npm run test:e2e:real
)
run_gate "package restart" "${SMOKE_ROOT}/install/poseidon" restart --no-browser --port 5090
run_gate "package status pós-restart" "${SMOKE_ROOT}/install/poseidon" status
run_gate "package stop" "${SMOKE_ROOT}/install/poseidon" stop
run_gate "package doctor" "${SMOKE_ROOT}/install/poseidon" doctor
export POSEIDON_DATA_DIR="${SMOKE_ROOT}/demo-data"
run_gate "first-run demo" "${SMOKE_ROOT}/install/poseidon" start --no-browser --port 5090 --demo
run_gate "first-run demo status" "${SMOKE_ROOT}/install/poseidon" status
run_gate "first-run demo stop" "${SMOKE_ROOT}/install/poseidon" stop
export POSEIDON_DATA_DIR="${SMOKE_ROOT}/data"

for resource in \
  "$(docker ps -aq --filter label=com.harness.managed=true)" \
  "$(docker volume ls -q --filter label=com.harness.managed=true)" \
  "$(docker network ls -q --filter label=com.harness.managed=true)"; do
  [[ -z "${resource}" ]] || fail "recurso Docker Harness órfão: ${resource}."
done

[[ -z "$(git status --short -- frontend docs/frontend)" ]] || fail "área frontend protegida contém alterações locais."
[[ -z "$(git status --porcelain)" ]] || fail "working tree ficou suja durante a geração do RC."

cat >"${TEST_REPORT}" <<EOF
# Relatório técnico da Release Candidate

- Commit: \`${SHA}\`
- RID: \`${RID}\`
- Gerado em UTC: \`$(date -u +%Y-%m-%dT%H:%M:%SZ)\`
- Resultado: todos os gates automatizados abaixo terminaram com exit 0.

## Gates executados

- regressão integral backend/frontend, build sem warnings e contract drift;
- governance linter, SAST, secret scanning e SBOM;
- SQLite/PostgreSQL, concorrência, recovery e resiliência;
- E2E frontend mock, a11y e build Storybook;
- pacote self-contained: instalação limpa, start, status, E2E contra API real, restart, stop e doctor;
- inventário final sem recurso Docker gerenciado órfão;
- working tree e áreas protegidas limpas.

O log bruto reproduzível está em \`gates.log\`. Aceites humanos continuam separados.
EOF

cp docs/backend/security/sbom.json "${RELEASE_ROOT}/sbom.json"
cp docs/backend/release/RELEASE_NOTES.md "${RELEASE_ROOT}/RELEASE_NOTES.md"
cp docs/backend/operations/INSTALLATION.md "${RELEASE_ROOT}/INSTALLATION.md"
cp docs/backend/operations/HOMOLOGATION.md "${RELEASE_ROOT}/HOMOLOGATION.md"

# Normalizar metadados e ordenar entradas torna o arquivo estável para o mesmo commit/toolchain.
find "${PACKAGE_DIR}" -exec touch -t 202001010000 {} +
(
  cd "${PACKAGE_DIR}"
  find . -type f -print | LC_ALL=C sort >"${RELEASE_ROOT}/package-files.txt"
  COPYFILE_DISABLE=1 tar --format ustar -cf - -T "${RELEASE_ROOT}/package-files.txt"
) | gzip -n >"${ARCHIVE}"
rm "${RELEASE_ROOT}/package-files.txt"

archive_sha="$(shasum -a 256 "${ARCHIVE}" | awk '{print $1}')"
archive_size="$(stat -f %z "${ARCHIVE}")"
screenshot_count="$(find "${RELEASE_ROOT}/screenshots" -type f | wc -l | tr -d ' ')"
cat >"${RELEASE_ROOT}/release-manifest.json" <<EOF
{
  "schemaVersion": 1,
  "product": "Poseidon",
  "commit": "${SHA}",
  "rid": "${RID}",
  "archive": "${PACKAGE_NAME}.tar.gz",
  "archiveBytes": ${archive_size},
  "archiveSha256": "${archive_sha}",
  "screenshots": ${screenshot_count},
  "packageManifest": "${PACKAGE_NAME}/.harness-desktop-package.json"
}
EOF

(
  cd "${RELEASE_ROOT}"
  {
    shasum -a 256 \
      "${PACKAGE_NAME}.tar.gz" \
      "${PACKAGE_NAME}/.harness-desktop-package.json" \
      HOMOLOGATION.md INSTALLATION.md RELEASE_NOTES.md TEST_REPORT.md gates.log \
      release-manifest.json sbom.json
    while IFS= read -r screenshot; do shasum -a 256 "${screenshot}"; done \
      < <(find screenshots -type f -print | LC_ALL=C sort)
  } >SHA256SUMS
  shasum -a 256 -c SHA256SUMS
)

trap - EXIT
cleanup
echo "release-candidate: todos os critérios técnicos automatizados estão verdes."
echo "Release Candidate: ${RELEASE_ROOT}"
echo "Pacote: ${ARCHIVE}"
echo "GNG-3, GNG-4, GNG-6 e integrações externas continuam sujeitos a aceite humano/credenciais."
