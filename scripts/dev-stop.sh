#!/usr/bin/env bash
# Stops the API, worker and dashboard. It NEVER removes the database: no compose "down", no
# volume removal, no file deletion. PostgreSQL keeps running and keeps its data; add --with-db to
# stop (not remove) its container too.
set -uo pipefail
cd "$(dirname "$0")/.."
for name in dashboard worker api; do
  pidfile=".run/$name.pid"
  if [ -f "$pidfile" ]; then
    pid="$(cat "$pidfile")"
    if kill -0 "$pid" 2>/dev/null; then kill "$pid" && echo "Stopped $name (pid $pid)."; else echo "$name was not running."; fi
    : > "$pidfile"
  fi
done
if [ "${1:-}" = "--with-db" ]; then
  docker compose -f infra/compose.yaml stop postgres && echo "Stopped the PostgreSQL container; its data volume is untouched."
fi
echo "Your database and files were not deleted."
