FROM python:3.13-alpine

LABEL com.harness.managed="true"

RUN addgroup -g 10001 harness \
    && adduser -D -H -u 10001 -G harness harness

COPY sandbox_service.py /opt/harness/sandbox_service.py

USER 10001:10001
ENTRYPOINT ["python", "/opt/harness/sandbox_service.py"]
