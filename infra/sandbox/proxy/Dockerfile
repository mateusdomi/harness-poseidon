FROM python:3.13-alpine

LABEL com.harness.managed="true"
LABEL com.harness.purpose="agent-execution-egress-proxy"

RUN addgroup -g 10001 harness \
    && adduser -D -H -u 10001 -G harness harness

COPY --chmod=0555 proxy_service.py /opt/harness/proxy_service.py

USER 10001:10001
ENTRYPOINT ["python", "/opt/harness/proxy_service.py"]
