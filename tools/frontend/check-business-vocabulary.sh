#!/usr/bin/env bash
# Gate de vocabulário do modo Negócio (F1): ponto de entrada do repositório.
# A implementação vive em `frontend/scripts/check-business-vocabulary.mjs`
# para viajar junto com o frontend nos builds empacotados.
set -euo pipefail

readonly REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

cd "${REPOSITORY_ROOT}/frontend"
exec node scripts/check-business-vocabulary.mjs "$@"
