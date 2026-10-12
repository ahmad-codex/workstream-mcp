#!/bin/sh
# All-in-one demo container: starts a throwaway Postgres 16 and the API in demo mode.
# Data lives in the container only and is lost when it is removed. Not for production.
set -eu

PGDATA=/var/lib/postgresql/data
export PGDATA

if [ ! -s "$PGDATA/PG_VERSION" ]; then
  mkdir -p "$PGDATA" /run/postgresql
  chown -R postgres:postgres "$PGDATA" /run/postgresql
  su-exec postgres initdb --auth=trust --username=postgres >/dev/null
fi
chown -R postgres:postgres /run/postgresql
su-exec postgres pg_ctl -D "$PGDATA" -o "-c listen_addresses=127.0.0.1" -w start >/dev/null

psql -h 127.0.0.1 -U postgres -tc "SELECT 1 FROM pg_roles WHERE rolname='workstream'" | grep -q 1 \
  || psql -h 127.0.0.1 -U postgres -c "CREATE ROLE workstream LOGIN SUPERUSER PASSWORD 'workstream-demo'" >/dev/null
psql -h 127.0.0.1 -U postgres -tc "SELECT 1 FROM pg_database WHERE datname='workstream'" | grep -q 1 \
  || psql -h 127.0.0.1 -U postgres -c "CREATE DATABASE workstream OWNER workstream" >/dev/null

export WORKSTREAM_DB_CONNECTION="Host=127.0.0.1;Port=5432;Database=workstream;Username=workstream;Password=workstream-demo"
export WORKSTREAM_DEMO=true

exec dotnet /app/Workstream.Api.dll "$@"
