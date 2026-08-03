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
readonly PROXY_CONTEXT="${REPOSITORY_ROOT}/infra/sandbox/proxy"
readonly PROXY_IMAGE="${HARNESS_SANDBOX_PROXY_IMAGE:-harness-sandbox-proxy:latest}"

if ! docker version --format '{{.Server.Version}}' >/dev/null 2>&1; then
  echo "Preciso do Docker para construir a imagem — instale ou inicie o Docker e me chame de novo." >&2
  exit 3
fi

# O proxy de egresso é a única porta de saída das sandboxes: sem a imagem dele toda sessão
# falha ao abrir (`sandbox.unavailable`) — e, como antes com a imagem do agente, nada dizia
# que ela era sequer necessária.
echo "Construindo ${PROXY_IMAGE} a partir de ${PROXY_CONTEXT}…"
docker build --tag "${PROXY_IMAGE}" "${PROXY_CONTEXT}"

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

# Verificação de que o proxy SERVE: um destino fora da allowlist precisa receber 403 — um
# proxy que aceita tudo é pior que nenhum, porque daria aparência de fronteira sem fronteira
# nenhuma. O cliente de teste é a própria imagem (python), sem dependência nova.
echo "Verificando o proxy de egresso…"
docker run --detach --rm --name harness-proxy-selftest "${PROXY_IMAGE}" >/dev/null
sleep 1
denied="$(docker run --rm --network container:harness-proxy-selftest \
  --entrypoint python "${PROXY_IMAGE}" -c "
import urllib.request
opener = urllib.request.build_opener(
    urllib.request.ProxyHandler({'https': 'http://127.0.0.1:8080'}))
try:
    opener.open('https://example.com/', timeout=5)
    print('ALLOWED')
except Exception as exc:
    print('403' if '403' in str(exc) else f'ERROR:{exc}')
" 2>/dev/null || true)"
docker rm --force harness-proxy-selftest >/dev/null 2>&1 || true
if [[ "${denied}" != "403" ]]; then
  echo "O proxy não negou um destino fora da allowlist (resposta: '${denied}')." >&2
  exit 5
fi

echo "Imagem ${PROXY_IMAGE} pronta e negando destino fora da allowlist."
