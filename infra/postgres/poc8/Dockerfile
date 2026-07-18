FROM alpine:3.24

LABEL org.opencontainers.image.title="Harness PostgreSQL PoC-8"

RUN apk add --no-cache postgresql18 postgresql18-contrib su-exec \
    && mkdir -p /var/lib/postgresql/data /run/postgresql \
    && chown -R postgres:postgres /var/lib/postgresql /run/postgresql

COPY --chmod=755 entrypoint.sh /usr/local/bin/harness-postgres-entrypoint

ENV PGDATA=/var/lib/postgresql/data

EXPOSE 5432

ENTRYPOINT ["/usr/local/bin/harness-postgres-entrypoint"]
CMD ["postgres"]
