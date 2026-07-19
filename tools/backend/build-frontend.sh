#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
frontend_source="$repo_root/frontend"
artifact_root="$repo_root/.artifacts"
embedded_target="$repo_root/src/Harness.Host/wwwroot"

test -f "$frontend_source/package.json"
test -f "$frontend_source/package-lock.json"
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
(
  cd "$frontend_work"
  npm ci
  npm run check
  VITE_API_MODE=http VITE_API_BASE_URL= npm run build
)

mkdir -p "$embedded_target"
rsync -a --delete "$frontend_work/dist/" "$embedded_target/"
