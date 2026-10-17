#!/usr/bin/env bash
# Smoke-tests the migrator image against a fresh PostgreSQL 17 (ADR-008, CI):
#   1. the first run applies every migration and seeds the initial symbols,
#   2. a second run is a no-op that still exits 0.
# Usage: scripts/ci/migrator-smoke.sh [migrator-image]   (default: marketdata/migrator:ci)
set -euo pipefail

IMAGE="${1:-marketdata/migrator:ci}"
NET="md-migrator-smoke-$$"
DB="md-migrator-smoke-pg-$$"
CONNECTION="Host=${DB};Port=5432;Database=marketdata;Username=postgres;Password=postgres;GSS Encryption Mode=Disable"

cleanup() {
  docker rm -f "$DB" >/dev/null 2>&1 || true
  docker network rm "$NET" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker network create "$NET" >/dev/null
docker run -d --name "$DB" --network "$NET" \
  -e POSTGRES_PASSWORD=postgres -e POSTGRES_DB=marketdata postgres:17-alpine >/dev/null

for _ in $(seq 1 60); do
  if docker exec "$DB" pg_isready -U postgres -d marketdata >/dev/null 2>&1; then break; fi
  sleep 1
done
docker exec "$DB" pg_isready -U postgres -d marketdata >/dev/null

psql() { docker exec "$DB" psql -U postgres -d marketdata -tAc "$1"; }

echo "== run 1: apply migrations"
docker run --rm --network "$NET" "$IMAGE" --connection "$CONNECTION"

applied=$(psql 'SELECT count(*) FROM "__EFMigrationsHistory"')
symbols=$(psql "SELECT string_agg(name, ',' ORDER BY name) FROM symbols WHERE is_active")
echo "migrations applied: ${applied}; active symbols: ${symbols}"
[ "$applied" -ge 1 ] || { echo "no migrations recorded" >&2; exit 1; }
[ "$symbols" = "ADAUSDT,BTCUSDT,ETHUSDT" ] || { echo "unexpected seed: ${symbols}" >&2; exit 1; }

echo "== run 2: must be a no-op"
output=$(docker run --rm --network "$NET" "$IMAGE" --connection "$CONNECTION")
echo "$output"
grep -q "No migrations were applied" <<<"$output" || { echo "second run was not a no-op" >&2; exit 1; }
[ "$(psql 'SELECT count(*) FROM "__EFMigrationsHistory"')" = "$applied" ] || { echo "migration history changed" >&2; exit 1; }

echo "migrator smoke test passed"
