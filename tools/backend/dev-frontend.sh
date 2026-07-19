#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
frontend_source="$repo_root/frontend"
artifact_root="$repo_root/.artifacts"
backend_url="${1:-http://127.0.0.1:5000}"

test -f "$frontend_source/package.json"
test -f "$frontend_source/package-lock.json"
mkdir -p "$artifact_root"
frontend_work="$(mktemp -d "$artifact_root/frontend-dev.XXXXXX")"

cleanup() {
  case "$frontend_work" in
    "$artifact_root"/frontend-dev.*) rm -rf "$frontend_work" ;;
    *) echo "Refusing to remove unexpected frontend work path: $frontend_work" >&2 ;;
  esac
}
trap cleanup EXIT

cp "$frontend_source/package.json" "$frontend_source/package-lock.json" "$frontend_work/"
cp "$repo_root/tools/backend/frontend-vite-proxy.config.ts" "$frontend_work/vite.config.ts"
(
  cd "$frontend_work"
  npm ci
  HARNESS_FRONTEND_ROOT="$frontend_source" \
  HARNESS_BACKEND_URL="$backend_url" \
  HARNESS_VITE_CACHE="$frontend_work/.vite" \
  VITE_API_MODE=http VITE_API_BASE_URL= \
  npm exec vite -- --config "$frontend_work/vite.config.ts"
)
