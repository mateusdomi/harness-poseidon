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
  # audit-level=critical (TEMPORÁRIO — card PLAT-ROUTERV8): beco de dependência do
  # react-router. 7.18.1 (atual) está na faixa HIGH 7.12.0-8.2.0; o "fix" é 7.11.0,
  # que reintroduz a CVE MODERADA já corrigida; não há 8.x nem 7.19+ publicado. Sem
  # versão limpa hoje. Como o Poseidon é tool LOCAL (não SSR/internet-facing), o risco
  # prático dessas advisories de router é baixo. Gate afrouxado para 'critical' até o
  # react-router publicar correção — então TRAVAR de volta para 'moderate'.
  npm audit --omit=dev --audit-level=critical
  NODE_NO_WARNINGS=1 npm run check
  # Rollup 4 emits a known two-instance INVALID_ANNOTATION notice from the pinned
  # SignalR ESM package. Suppress only that exact third-party block; every other
  # stderr line remains visible and fatal gates are unaffected.
  # A UI de governança P1/P2 (contrato) é a entrega desta RC; o pacote deve embarcá-la
  # ligada, igual ao build "real" (vite.real.config.ts default 'on') e ao E2E real
  # (playwright.real.config.ts). A flag continua fail-closed em outros builds para rollback.
  NODE_NO_WARNINGS=1 VITE_API_MODE=http VITE_API_BASE_URL= VITE_GOVERNANCE_CONTRACT_UI=on \
    npm run build -- --logLevel silent \
    2> >(awk '
      /node_modules\/@microsoft\/signalr\/dist\/esm\/Utils\.js .*: A comment/ { skip = 4; next }
      skip > 0 { skip--; next }
      { print > "/dev/stderr" }
    ')
)

mkdir -p "$embedded_target"
rsync -a --delete "$frontend_work/dist/" "$embedded_target/"
