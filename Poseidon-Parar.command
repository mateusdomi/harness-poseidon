#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Poseidon — PARAR (duplo-clique no Finder). Encerra o Host/daemon com graça.
# ---------------------------------------------------------------------------
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

echo "== Poseidon — parando =="
./poseidon stop || true
echo "Host encerrado. Pode fechar esta janela."
