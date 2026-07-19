#!/usr/bin/env bash
# Varre os arquivos rastreados pelo Git por segredos de alta precisão (token de bot
# Telegram, chave privada, AWS/Slack/Google). Exit != 0 se encontrar. Espelha o gate
# automatizado em tests/Harness.ArchitectureTests/SecretHygieneTests.cs.
set -euo pipefail
cd "$(dirname "$0")/../.."

patterns=(
  '[0-9]{6,10}:AA[A-Za-z0-9_-]{30,}'
  '-----BEGIN (RSA |EC |OPENSSH |DSA |PGP )?PRIVATE KEY-----'
  'AKIA[0-9A-Z]{16}'
  'xox[baprs]-[0-9A-Za-z-]{10,}'
  'AIza[0-9A-Za-z_-]{35}'
)

found=0
for pattern in "${patterns[@]}"; do
  if git grep -nE -e "$pattern" -- . ':(exclude)*.lock.json' ':(exclude)docs/backend/security/sbom.json'; then
    found=1
  fi
done

if [ "$found" -ne 0 ]; then
  echo "ERRO: possível segredo rastreado no Git." >&2
  exit 1
fi

echo "scan-secrets: nenhum segredo de alta precisão encontrado nos arquivos rastreados."
