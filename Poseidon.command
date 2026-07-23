#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# Poseidon — INICIAR (duplo-clique no Finder, sem terminal manual).
#
# Sobe o Host como daemon destacado (independente do Claude), abre o navegador
# na UI e deixa você homologar/planejar em paralelo. Idempotente: se já estiver
# no ar, só reabre o navegador.
#
# Este terminal só serve para subir o Host. Após o health check confirmar que
# tudo está OK, a janela se FECHA sozinha — o Host segue rodando em background.
# ---------------------------------------------------------------------------
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"

# Fecha SOMENTE a janela do Terminal que rodou este script (macOS/Terminal.app),
# sem prompt de "encerrar processos": aguarda o shell sair e então fecha por tty.
close_own_terminal_window() {
  [[ "${TERM_PROGRAM:-}" == "Apple_Terminal" ]] || return 0
  local tty_dev; tty_dev="$(tty 2>/dev/null)" || return 0
  ( sleep 1
    osascript >/dev/null 2>&1 <<OSA
tell application "Terminal"
  repeat with w in windows
    repeat with t in tabs of w
      try
        if tty of t is "${tty_dev}" then
          close w
          return
        end if
      end try
    end repeat
  end repeat
end tell
OSA
  ) &
  disown 2>/dev/null || true
}

echo "== Poseidon =="
if ./poseidon status >/dev/null 2>&1; then
  addr="$(./poseidon status 2>/dev/null | sed -n 's/.*\(http:\/\/[0-9.]*:[0-9]*\).*/\1/p' | head -1)"
  echo "Já estava no ar em ${addr:-http://127.0.0.1}. Reabrindo o navegador…"
  [[ -n "${addr}" ]] && /usr/bin/open "${addr}"
else
  echo "Subindo o Host (pode levar alguns segundos)…"
  ./poseidon start
fi

# Health check: ./poseidon status roda o probe operacional real (endpoint
# health + API). Confirma OK antes de fechar a janela — com algumas tentativas
# para dar tempo de warmup.
echo -n "Health check"
healthy=0
for _ in 1 2 3 4 5 6 7 8 9 10; do
  if ./poseidon status >/dev/null 2>&1; then healthy=1; break; fi
  echo -n "."
  sleep 1
done
echo

if [[ "${healthy}" -eq 1 ]]; then
  echo "✅ Host operacional. Esta janela vai se fechar sozinha — o Host continua rodando."
  close_own_terminal_window
else
  echo "⚠️  O Host subiu mas o health check não confirmou. Janela mantida aberta para você ver o motivo:"
  echo "    ./poseidon status   e   ./poseidon logs"
fi
