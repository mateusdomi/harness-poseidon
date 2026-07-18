#!/bin/sh
set -eu

if [ "${1:-}" != "postgres" ]; then
    exec "$@"
fi

if [ "$(id -u)" = "0" ]; then
    mkdir -p "$PGDATA" /run/postgresql
    chown -R postgres:postgres "$PGDATA" /run/postgresql
    install -o postgres -g postgres -m 600 "$POSTGRES_PASSWORD_FILE" /run/postgresql/postgres-password
    export POSTGRES_PASSWORD_FILE=/run/postgresql/postgres-password
    exec su-exec postgres "$0" "$@"
fi

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${POSTGRES_PASSWORD_FILE:?POSTGRES_PASSWORD_FILE is required}"

if [ ! -s "$POSTGRES_PASSWORD_FILE" ]; then
    echo "POSTGRES_PASSWORD_FILE is empty or unavailable" >&2
    exit 1
fi

if [ ! -s "$PGDATA/PG_VERSION" ]; then
    initdb \
        --pgdata="$PGDATA" \
        --username="$POSTGRES_USER" \
        --pwfile="$POSTGRES_PASSWORD_FILE" \
        --auth-host=scram-sha-256 \
        --auth-local=trust
    printf '%s\n' 'host all all all scram-sha-256' >> "$PGDATA/pg_hba.conf"

    pg_ctl \
        --pgdata="$PGDATA" \
        --options="-c listen_addresses='' -c unix_socket_directories=/run/postgresql" \
        --wait \
        start
    createdb \
        --host=/run/postgresql \
        --username="$POSTGRES_USER" \
        --owner="$POSTGRES_USER" \
        "$POSTGRES_DB"
    pg_ctl --pgdata="$PGDATA" --mode=fast --wait stop
fi

exec postgres -D "$PGDATA" -c listen_addresses='*'
