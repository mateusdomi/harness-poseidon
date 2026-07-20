#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
frontend_source="$repo_root/frontend"
artifact_root="$repo_root/.artifacts"
embedded_target="$repo_root/src/Harness.Host/wwwroot"
contract_source="$repo_root/docs/contracts"
contract_target="$artifact_root/docs/contracts"

test -f "$frontend_source/package.json"
test -f "$frontend_source/package-lock.json"
test -f "$contract_source/events.json"
test -f "$contract_source/openapi.json"
mkdir -p "$artifact_root"
frontend_work="$(mktemp -d "$artifact_root/frontend-build.XXXXXX")"

cleanup() {
  case "$frontend_work" in
    "$artifact_root"/frontend-build.*) rm -rf "$frontend_work" ;;
    *) echo "Refusing to remove unexpected frontend work path: $frontend_work" >&2 ;;
  esac
}
trap cleanup EXIT

rsync -a --exclude node_modules --exclude dist "$frontend_source/" "$frontend_work/"
mkdir -p "$contract_target"
rsync -a "$contract_source/events.json" "$contract_target/events.json"
rsync -a "$contract_source/openapi.json" "$contract_target/openapi.json"
(
  cd "$frontend_work"
  npm ci --no-audit --loglevel=error
  npm audit --omit=dev --audit-level=moderate
  NODE_NO_WARNINGS=1 npm run check
  # Rollup 4 emits a known two-instance INVALID_ANNOTATION notice from the pinned
  # SignalR ESM package. Suppress only that exact third-party block; every other
  # stderr line remains visible and fatal gates are unaffected.
  NODE_NO_WARNINGS=1 VITE_API_MODE=http VITE_API_BASE_URL= \
    npm run build -- --logLevel silent \
    2> >(awk '
      /node_modules\/@microsoft\/signalr\/dist\/esm\/Utils\.js .*: A comment/ { skip = 4; next }
      skip > 0 { skip--; next }
      { print > "/dev/stderr" }
    ')
)

mkdir -p "$embedded_target"
rsync -a --delete "$frontend_work/dist/" "$embedded_target/"
