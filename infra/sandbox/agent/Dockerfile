# Imagem de execução dos agentes do Poseidon.
#
# É o ambiente isolado onde TODA execução acontece desde o 0-E — o contêiner é o caminho único, e
# não existe mais aceite de risco que o dispense.
#
# ── O que entra, e por quê ────────────────────────────────────────────────────────────────────
#
#   claude  — Claude Code. Serve a duas famílias de conta: a Anthropic e a GLM, que fala o mesmo
#             protocolo com outra base URL (`ExecutorCatalog.Glm` reusa este binário).
#   codex   — Codex CLI (OpenAI).
#
# Ficam DE FORA, por decisão:
#
#   agy (Antigravity) — é o crítico read-only, roda no host e não executa trabalho de projeto.
#                       Embarcá-lo aumentaria a superfície do contêiner sem nenhum ganho.
#   kimi              — não tem adapter implementado (a Fase 1 recusou o item, e o executor falha
#                       explícito com `executor.adapter_not_implemented`) e a conta está sem cota.
#                       Instalar um CLI que o produto não sabe acionar é peso morto na imagem.
#
# Modelos LOCAIS e novos provedores entram por aqui: acrescente o CLI, versione a mudança e
# reconstrua. O catálogo de executores já é dado, não código.
#
# ── Credenciais: NENHUMA nesta imagem ─────────────────────────────────────────────────────────
#
# Nada de token, chave ou sessão em camada, ARG ou ENV. Um segredo numa layer fica no histórico da
# imagem para sempre — quem tiver a imagem tem o segredo, mesmo que a camada seguinte o apague.
#
# A autenticação entra em RUNTIME, por montagem somente-leitura do config home ISOLADO daquela
# conta (`AccountProfileProvisioner` já cria um por alias e exporta a variável que cada CLI espera:
# `CLAUDE_CONFIG_DIR`, `CODEX_HOME`, …). Assim uma conta nunca enxerga o perfil da outra, e revogar
# acesso é apagar um diretório do host — não reconstruir imagem.

FROM node:22-bookworm-slim

LABEL com.harness.managed="true"
LABEL com.harness.purpose="agent-execution-sandbox"

# git é pré-requisito real: o agente trabalha numa worktree e precisa ler o histórico para não
# reescrever o que não entendeu. ca-certificates para TLS; o resto fica fora — cada pacote a mais
# é superfície de ataque num ambiente que executa código de terceiros.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        ca-certificates \
        python3 \
        python3-pip \
        libatomic1 \
        ffmpeg \
        espeak-ng \
        unzip \
        poppler-utils \
        tesseract-ocr \
        tesseract-ocr-eng \
        tesseract-ocr-por \
    && rm -rf /var/lib/apt/lists/*

# Transcrição local e sem egress. O modelo pequeno em português fica dentro da imagem,
# com checksum fixo; assim um áudio nunca depende de baixar pesos durante a execução nem é
# enviado a um serviço externo. A versão do motor também é fixa para tornar a evidência
# reproduzível.
RUN python3 -m pip install --no-cache-dir --break-system-packages vosk==0.3.45
ADD --checksum=sha256:6e1ce909032e1afa7a88e68a3d628ecafff302bdf195befab308826c395e93b7 \
    https://alphacephei.com/vosk/models/vosk-model-small-pt-0.3.zip /tmp/vosk-model-pt.zip
RUN mkdir -p /opt/harness/models \
    && unzip -q /tmp/vosk-model-pt.zip -d /opt/harness/models \
    && mv /opt/harness/models/vosk-model-small-pt-0.3 /opt/harness/models/vosk-pt \
    && rm /tmp/vosk-model-pt.zip

# Versões FIXAS. Um `latest` faz a mesma imagem produzir comportamentos diferentes em dias
# diferentes, e aí nenhuma execução é reproduzível — o oposto do que uma sandbox atestada promete.
ARG CLAUDE_CODE_VERSION=2.0.30
ARG CODEX_VERSION=0.44.0

RUN npm install -g --no-fund --no-audit \
        "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
        "@openai/codex@${CODEX_VERSION}" \
    && npm cache clean --force

COPY --chmod=0555 artifact_extract.py /opt/harness/artifact_extract.py
COPY --chmod=0555 artifact_extract_selftest.py /opt/harness/artifact_extract_selftest.py

# Usuário sem privilégio, com UID estável: o mesmo do PoC (10001), para que a worktree montada do
# host tenha dono previsível e o agente não precise de root para escrever no próprio espaço.
RUN groupadd -g 10001 harness \
    && useradd -m -u 10001 -g harness -s /bin/bash harness

USER 10001:10001
WORKDIR /workspace

# Sem ENTRYPOINT fixo: quem decide o comando é o adapter do executor, que já monta os argumentos
# por conta e por papel. Fixar aqui duplicaria essa decisão em dois lugares e faria a imagem
# discordar do catálogo na primeira mudança de flag.
CMD ["bash", "-lc", "claude --version && codex --version"]
