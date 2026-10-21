#!/usr/bin/env bash
# Write a verified backup (database dump + every asset with its SHA-256) into a NEW directory.
# Usage: scripts/backup.sh [directory]   (default: ~/ContentFactoryBackups/<UTC timestamp>)
# Holds no environment secrets. Copy the result to an encrypted off-device location yourself.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
[ -f .env ] && { set -a; source .env; set +a; }
target="${1:-$HOME/ContentFactoryBackups/$(date -u +%Y%m%dT%H%M%SZ)}"
umask 077
dotnet run --project src/ContentFactory.Cli --no-launch-profile -- backup "$target"
