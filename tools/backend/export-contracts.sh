#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository_root="$(cd "$script_dir/../.." && pwd)"
artifact_root="$script_dir/.tooling/contract-export"
log_path="$artifact_root/host.log"
raw_openapi="$artifact_root/openapi.raw.json"
host_pid=""

cleanup() {
  if [[ -n "$host_pid" ]] && kill -0 "$host_pid" 2>/dev/null; then
    kill "$host_pid" 2>/dev/null || true
    wait "$host_pid" 2>/dev/null || true
  fi
}
trap cleanup EXIT INT TERM

mkdir -p "$artifact_root"
: > "$log_path"

"$script_dir/dotnet.sh" build "$repository_root/src/Harness.Host/Harness.Host.csproj" \
  --configuration Release --no-restore >/dev/null

"$script_dir/dotnet.sh" \
  "$repository_root/src/Harness.Host/bin/Release/net10.0/Harness.Host.dll" \
  --urls http://127.0.0.1:0 >"$log_path" 2>&1 &
host_pid=$!

address=""
for _ in $(seq 1 100); do
  address="$(rg -o 'http://127\.0\.0\.1:[0-9]+' "$log_path" | tail -n 1 || true)"
  if [[ -n "$address" ]]; then
    break
  fi
  if ! kill -0 "$host_pid" 2>/dev/null; then
    sed -n '1,200p' "$log_path" >&2
    exit 1
  fi
  sleep 0.1
done

if [[ -z "$address" ]]; then
  echo "Harness.Host did not publish its dynamic loopback address." >&2
  exit 1
fi

curl --fail --silent --show-error "$address/openapi/v1.json" --output "$raw_openapi"
python3 -m json.tool "$raw_openapi" > "$repository_root/docs/contracts/openapi.json"
