#!/usr/bin/env bash
# Apply migrations against the running api container's Postgres connection.
# Runs after `docker compose pull && docker compose up -d` in the deploy flow (§12.4).

set -euo pipefail

cd "$(dirname "$0")/.."

# Make sure postgres is healthy before attempting migrations.
docker compose -f deploy/docker-compose.yml ps postgres | grep -q "(healthy)" || {
  echo "postgres is not healthy yet — waiting"
  for i in {1..30}; do
    sleep 2
    if docker compose -f deploy/docker-compose.yml ps postgres | grep -q "(healthy)"; then
      break
    fi
  done
}

# The API applies migrations on startup (see Program.cs). For an explicit one-shot apply
# we just restart the api container; the SqlMigrationRunner runs before the HTTP server binds.
docker compose -f deploy/docker-compose.yml restart api
