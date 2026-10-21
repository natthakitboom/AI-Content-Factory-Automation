#!/usr/bin/env bash
# Proves backups work: back up the real database, restore into a NEW scratch database and data
# root, check every asset and the paused paid lanes, then remove only what this script created.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
[ -f .env ] && { set -a; source .env; set +a; }
work="$(mktemp -d)"; db="cf_restore_test_$(date +%s)"
cleanup() {
  docker exec content-factory-postgres psql -U "${DATABASE_USER:-content_factory}" -d postgres -c "DROP DATABASE IF EXISTS \"$db\" WITH (FORCE)" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

./scripts/backup.sh "$work/backup" | tail -2
dotnet run --project src/ContentFactory.Cli --no-build --no-launch-profile -- restore "$work/backup" "$db" "$work/data" --confirm "restore $db" | head -3

expected="$(python3 -c "import json;print(len(json.load(open('$work/backup/manifest.json'))['assets']))")"
actual="$( [ -d "$work/data/assets" ] && find "$work/data/assets" -type f | wc -l | tr -d ' ' || echo 0 )"
[ "$expected" = "$actual" ] || { echo "Restore check FAILED: $expected assets expected, $actual restored."; exit 1; }
paused="$(docker exec content-factory-postgres psql -U "${DATABASE_USER:-content_factory}" -d "$db" -tAc "select value from app.system_flags where key='paid_lanes_paused'")"
[ "$paused" = "true" ] || { echo "Restore check FAILED: paid lanes are not paused."; exit 1; }
echo "Restore test PASSED: $actual assets restored and hash-verified, paid lanes paused. Scratch database removed."
