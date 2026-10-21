#!/usr/bin/env bash
# One command that runs every gate and tells the truth about each: PASS, FAIL, or BLOCKED (a
# prerequisite is missing, which is never reported as a pass). Usage: scripts/verify-all.sh [--fast]
# Verification uses mock providers only. It never needs, reads or sends a paid key, and the
# environment below pins paid work off for every child process.
set -uo pipefail
cd "$(dirname "$0")/.."

export ALLOW_PAID_APIS=false AUTO_PUBLISH=false PUBLISHING_MODE=manual_export
export TEXT_PROVIDER=mock VIDEO_PROVIDER=mock VOICE_PROVIDER=mock ENABLE_SCHEDULES=false
export FAL_KEY= AZURE_SPEECH_KEY= LM_STUDIO_API_TOKEN= LIVE_APPROVAL_FILE=

export PATH="$HOME/.dotnet:$PATH"
[ -x "$HOME/.node/v$(cat .node-version)/bin/node" ] && export PATH="$HOME/.node/v$(cat .node-version)/bin:$PATH"
[ -x /opt/homebrew/opt/ffmpeg-full/bin/ffmpeg ] && export PATH="/opt/homebrew/opt/ffmpeg-full/bin:$PATH"
if [ -z "${CF_TEST_POSTGRES:-}" ] && [ -f .env.test.local ]; then export "$(cat .env.test.local)"; fi

fast=0; [ "${1:-}" = "--fast" ] && fast=1
results=(); failed=0; blocked=0

gate() { # name command...
  local name="$1"; shift
  echo; echo "== $name =="
  "$@"; local code=$?
  case "$code" in
    0) results+=("PASS     $name") ;;
    2) results+=("BLOCKED  $name"); blocked=1 ;;
    *) results+=("FAIL     $name (exit $code)"); failed=1 ;;
  esac
}

need_db() { [ -n "${CF_TEST_POSTGRES:-}" ] || { echo "CF_TEST_POSTGRES is not set."; return 2; }; }
need_media() { command -v ffmpeg >/dev/null && command -v ffprobe >/dev/null || { echo "FFmpeg/FFprobe missing."; return 2; }; }
need_node() { [ "$(node --version 2>/dev/null)" = "v$(cat .node-version)" ] || { echo "Node $(cat .node-version) is required."; return 2; }; }

gate "secret scan" ./scripts/verify-no-secrets.sh
gate "build (0 warnings, 0 errors)" bash -c 'dotnet build ContentFactory.slnx --nologo -v q -warnaserror 2>&1 | tail -3'
gate "unit tests" dotnet test tests/ContentFactory.UnitTests --nologo
gate "integration tests (PostgreSQL)" bash -c "$(declare -f need_db); need_db && dotnet test tests/ContentFactory.IntegrationTests --nologo"
gate "dashboard typecheck + unit tests" bash -c "$(declare -f need_node); need_node && cd apps/dashboard && npm ci --no-audit --no-fund >/dev/null && npm run typecheck && npm test"
if [ "$fast" -eq 0 ]; then
  gate "media tests (real FFmpeg)" bash -c "$(declare -f need_media); need_media && dotnet test tests/ContentFactory.MediaTests --nologo"
  gate "end-to-end mock flow (Playwright)" bash -c "$(declare -f need_media need_node); need_media && need_node && cd apps/dashboard && npm run test:e2e"
else
  results+=("SKIPPED  media tests and end-to-end (--fast)")
fi

echo; echo "== Summary =="; printf '%s\n' "${results[@]}"
[ "$failed" -eq 1 ] && exit 1
[ "$blocked" -eq 1 ] && { echo "Some gates were BLOCKED, so this is not a full pass."; exit 2; }
echo "All gates passed with mock providers only."
