FROM python:3.13-alpine

LABEL com.harness.managed="true"

RUN addgroup -g 10001 harness \
    && adduser -D -H -u 10001 -G harness harness

COPY sandbox_service.py /opt/harness/sandbox_service.py

# Shim no PATH: a sessão de processo entra no contêiner por `docker exec`, que NÃO passa pelo
# ENTRYPOINT — o executável pedido precisa existir como binário. O modo de serviço (proxy,
# target, probe) continua saindo pelo ENTRYPOINT no caminho de `docker run`.
RUN printf '#!/bin/sh\nexec python /opt/harness/sandbox_service.py fake-codex "$@"\n' \
        > /usr/local/bin/fake-codex \
    && chmod 0555 /usr/local/bin/fake-codex

USER 10001:10001
ENTRYPOINT ["python", "/opt/harness/sandbox_service.py"]
