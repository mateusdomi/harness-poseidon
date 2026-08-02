#!/usr/bin/env bash
#
# Constrói a imagem de execução dos agentes.
#
# Existe porque o produto EXIGIA uma imagem que ele não construía nem ensinava a construir: no
# piloto real o Docker estava de pé, o doctor verde, e toda execução era recusada com
# `sandbox_required` — vinte e uma tentativas do mesmo card canceladas em milissegundos, sem que
# nada dissesse que faltava uma imagem.
#
# Nenhuma credencial entra aqui. A autenticação de cada conta é montada em runtime, somente
# leitura, a partir do config home isolado que o `AccountProfileProvisioner` mantém no host.
set -euo pipefail

readonly TOOLS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
readonly REPOSITORY_ROOT="$(cd "${TOOLS_DIR}/../.." && pwd)"
readonly CONTEXT="${REPOSITORY_ROOT}/infra/sandbox/agent"
readonly IMAGE="${HARNESS_SANDBOX_IMAGE:-harness-sandbox-agent:latest}"

if ! docker version --format '{{.Server.Version}}' >/dev/null 2>&1; then
  echo "Preciso do Docker para construir a imagem — instale ou inicie o Docker e me chame de novo." >&2
  exit 3
fi

echo "Construindo ${IMAGE} a partir de ${CONTEXT}…"
docker build --tag "${IMAGE}" "${CONTEXT}"

# Verificação de que a imagem SERVE, e não apenas de que foi construída: um build verde com um CLI
# quebrado dentro devolveria o produto ao mesmo sintoma silencioso.
echo "Verificando os executores dentro da imagem…"
docker run --rm --network=none "${IMAGE}" \
  bash -lc 'claude --version && codex --version'

extraction="$({ docker run --rm --network=none --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=32m \
  --cap-drop ALL --security-opt no-new-privileges \
  --mount "type=bind,source=${REPOSITORY_ROOT}/governance/core.md,target=/input/payload,readonly" \
  "${IMAGE}" python3 /opt/harness/artifact_extract.py /input/payload text/markdown; } 2>/dev/null)"
if ! jq -e '.status == "extracted" and (.text | contains("Núcleo de governança"))' \
  >/dev/null <<<"${extraction}"; then
  echo "A imagem subiu, mas o extrator isolado não conseguiu ler o fixture textual." >&2
  exit 4
fi

echo "Verificando extração de texto, PDF, DOCX, XLSX, imagem e transcrição de áudio…"
docker run --rm --network=none --read-only \
  --tmpfs /tmp:rw,noexec,nosuid,size=64m \
  --cap-drop ALL --security-opt no-new-privileges \
  "${IMAGE}" python3 /opt/harness/artifact_extract_selftest.py

echo "Imagem ${IMAGE} pronta."
