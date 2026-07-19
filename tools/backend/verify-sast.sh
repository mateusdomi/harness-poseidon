#!/usr/bin/env bash
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly IMAGE="semgrep/semgrep:1.170.0-nonroot@sha256:c63f1dbe9339bc95351bdaac80bf0d9b5abed37789a04eaa6dfa16c85a4687d3"
readonly CONTAINER_NAME="harness-semgrep-sast"

cd "${REPOSITORY_ROOT}"

cleanup() {
  local status=$?
  if docker inspect "${CONTAINER_NAME}" >/dev/null 2>&1; then
    managed="$(docker inspect --format '{{ index .Config.Labels "com.harness.managed" }}' "${CONTAINER_NAME}")"
    if [[ "${managed}" == "true" ]]; then
      docker rm --force "${CONTAINER_NAME}" >/dev/null
    else
      echo "verify-sast: recusado cleanup de container sem label Harness." >&2
      status=1
    fi
  fi
  exit "${status}"
}
trap cleanup EXIT

docker run --rm \
  --name "${CONTAINER_NAME}" \
  --label com.harness.managed=true \
  --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=256m \
  --tmpfs /home/semgrep:rw,noexec,nosuid,size=32m,uid=1000,gid=1000,mode=0700 \
  --env SEMGREP_SETTINGS_FILE=/tmp/semgrep-settings.yml \
  --security-opt no-new-privileges \
  --cap-drop ALL \
  --pids-limit 256 \
  --memory 1g \
  --cpus 2 \
  --volume "${REPOSITORY_ROOT}:/src:ro" \
  --workdir /src \
  "${IMAGE}" \
  semgrep scan \
  --config p/csharp \
  --config tools/backend/semgrep.yml \
  --exclude-rule csharp.lang.security.sqli.csharp-sqli.csharp-sqli \
  --exclude frontend \
  --exclude src/Harness.Host/wwwroot \
  --exclude .artifacts \
  --metrics=off \
  --error \
  src tests

echo "verify-sast: Semgrep dedicado sem achados bloqueantes."
