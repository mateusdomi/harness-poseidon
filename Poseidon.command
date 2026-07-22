#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Poseidon — INICIAR (duplo-clique no Finder, sem terminal manual).
#
# Sobe o Host como daemon destacado (independente do Claude), abre o navegador
# na UI e deixa você homologar/planejar em paralelo. Idempotente: se já estiver
# no ar, só reabre o navegador. Fecha esta janela quando quiser — o Host continua.
# ---------------------------------------------------------------------------
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

echo "== Poseidon =="
if ./poseidon status >/dev/null 2>&1; then
  addr="$(./poseidon status 2>/dev/null | sed -n 's/.*\(http:\/\/[0-9.]*:[0-9]*\).*/\1/p' | head -1)"
  echo "Já estava no ar em ${addr:-http://127.0.0.1}. Reabrindo o navegador…"
  [[ -n "${addr}" ]] && /usr/bin/open "${addr}"
else
  echo "Subindo o Host (pode levar alguns segundos)…"
  ./poseidon start
fi
echo
echo "Pronto. Pode fechar esta janela — o Host continua rodando em segundo plano."
echo "Para parar depois: duplo-clique em Poseidon-Parar.command"
