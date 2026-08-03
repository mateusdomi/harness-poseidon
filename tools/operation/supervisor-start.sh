#!/usr/bin/env bash
# Sobe o supervisor da Operação Final desacoplado do terminal.
#
# Existe porque a supervisão precisa sobreviver à sessão que a criou: se o supervisor morre
# junto com a janela da Integradora, ele não supervisiona nada — só acompanha. `nohup` e a
# redireção completa das três descritoras são o que faz o processo continuar depois que o
# terminal fecha.
#
# O binário é publicado antes: `dotnet run` recompila, e recompilar às 3 da manhã com o
# disco sob pressão é uma forma criativa de a operação parar sozinha.
#
# Uso:  tools/operation/supervisor-start.sh [PID-da-integradora-atual]
#
# Passando um PID, o supervisor entende que já existe uma Integradora viva: ele observa e só
# lança a sucessora quando aquele processo morrer (§12 — uma Integradora por vez).
set -euo pipefail

cd "$(dirname "$0")/../.."
ROOT="coordination/final-operation"
DOTNET="tools/backend/.tooling/dotnet/dotnet"
PUBLISH=".tooling/supervisor"
LOG="$ROOT/supervisor.log"

export POSEIDON_INTEGRATOR_COMMAND="${POSEIDON_INTEGRATOR_COMMAND:-claude -p --permission-mode bypassPermissions}"
export POSEIDON_SUPERVISOR_COOLDOWN_SECONDS="${POSEIDON_SUPERVISOR_COOLDOWN_SECONDS:-30}"
export POSEIDON_SUPERVISOR_MAX_CYCLES="${POSEIDON_SUPERVISOR_MAX_CYCLES:-100}"

# A cerca nao pode depender so do ARQUIVO: apagar o lease a mao (foi o que eu fiz em
# 03/08/2026) deixava subir um segundo supervisor com o primeiro vivo — exatamente o
# duplo-relance que o lease existe para impedir. Processo vivo manda mais que arquivo.
if pgrep -f "Harness.OperationSupervisor.dll run" >/dev/null 2>&1; then
  echo "supervisor já em execução (processo vivo). Nada a fazer."
  exit 0
fi

if [ -f "$ROOT/LEASE.json" ]; then
  pid=$(python3 -c "import json,sys;print(json.load(open('$ROOT/LEASE.json'))['pid'])" 2>/dev/null || echo "")
  if [ -n "$pid" ] && kill -0 "$pid" 2>/dev/null; then
    echo "supervisor já ativo (pid $pid). Nada a fazer."
    exit 0
  fi
  echo "lease órfão encontrado; será retomado."
fi

if [ "${1:-}" != "" ]; then
  python3 - "$1" <<'PY'
import json, sys
from datetime import datetime, timezone
pid = int(sys.argv[1])
payload = {"pid": pid, "source": "sessao-interativa", "since": datetime.now(timezone.utc).isoformat()}
with open("coordination/final-operation/INTEGRATOR.json", "w") as handle:
    json.dump(payload, handle, indent=2)
PY
  echo "Integradora atual registrada: pid $1 (o supervisor vai observar até ela sair)."
fi

"$DOTNET" publish src/Harness.OperationSupervisor/Harness.OperationSupervisor.csproj \
  -c Release -o "$PUBLISH" --nologo -v q >/dev/null

nohup "$DOTNET" "$PUBLISH/Harness.OperationSupervisor.dll" run >>"$LOG" 2>&1 &
echo "supervisor iniciado: pid $! — log em $LOG"
