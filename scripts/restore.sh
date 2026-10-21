#!/usr/bin/env bash
# Restore a backup into a NEW database and a NEW data root. It never overwrites anything.
# Usage: scripts/restore.sh <backup-dir> <new-database> <new-data-root>
# You will be asked to type the confirmation. Paid lanes stay paused afterwards.
set -euo pipefail
if [ "$#" -ne 3 ]; then
  echo "Usage: $0 <backup-dir> <new-database> <new-data-root>" >&2
  exit 2
fi
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
[ -f .env ] && { set -a; source .env; set +a; }
echo "This restores into database '$2' and directory '$3'. Neither may exist yet."
read -r -p "Type 'restore $2' to continue: " answer
dotnet run --project src/ContentFactory.Cli --no-launch-profile -- restore "$1" "$2" "$3" --confirm "$answer"
