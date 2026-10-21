#!/usr/bin/env bash
# Offline smoke test: fixture product -> campaign -> run -> poll/download -> render -> QA,
# repeated N times. Mock providers only; the tests assert zero external network requests,
# zero billable provider requests, zero budget reservations and no automatic approval.
# This script additionally checks that no temp directories or unfinished jobs leak.
set -euo pipefail

ROUNDS="${1:-10}"
cd "$(dirname "$0")/.."

export PATH="$HOME/.dotnet:$PATH"
[ -x /opt/homebrew/opt/ffmpeg-full/bin/ffmpeg ] && export PATH="/opt/homebrew/opt/ffmpeg-full/bin:$PATH"

# Paid or live behaviour must never be enabled by a smoke run.
export ALLOW_PAID_APIS=false AUTO_PUBLISH=false PUBLISHING_MODE=manual_export
export TEXT_PROVIDER=mock VIDEO_PROVIDER=mock

for tool in ffmpeg ffprobe; do
  command -v "$tool" >/dev/null 2>&1 || { echo "$tool is not installed; smoke is BLOCKED (not passed)."; exit 2; }
done
filters="$(ffmpeg -hide_banner -filters 2>/dev/null || true)"
grep -q ' subtitles ' <<<"$filters" \
  || { echo "ffmpeg lacks libass (install ffmpeg-full); smoke is BLOCKED (not passed)."; exit 2; }

if [ -z "${CF_TEST_POSTGRES:-}" ] && [ -f .env.test.local ]; then
  export "$(cat .env.test.local)"
fi
[ -n "${CF_TEST_POSTGRES:-}" ] || { echo "CF_TEST_POSTGRES is not set; smoke is BLOCKED (not passed)."; exit 2; }

TMP_ROOT="${TMPDIR:-/tmp}"
count_temp() {
  find "$TMP_ROOT/contentfactory-e2e-tests" "$TMP_ROOT/cf-mock-clip" -mindepth 1 -maxdepth 1 2>/dev/null | wc -l | tr -d ' '
}

before="$(count_temp)"
dotnet build tests/ContentFactory.IntegrationTests --nologo -v q >/dev/null

for round in $(seq 1 "$ROUNDS"); do
  echo "== smoke round $round/$ROUNDS =="
  dotnet test tests/ContentFactory.IntegrationTests --no-build --nologo \
    --filter "EndToEndPipelineTests" | grep -E "Passed!|Failed!|Skipped!" | tee /tmp/cf-smoke-last.txt
  grep -q "Failed: *0" /tmp/cf-smoke-last.txt || { echo "Smoke FAILED in round $round."; exit 1; }
  grep -q "Skipped: *0" /tmp/cf-smoke-last.txt || { echo "Smoke SKIPPED tests in round $round; that is not a pass."; exit 2; }
done
rm -f /tmp/cf-smoke-last.txt

after="$(count_temp)"
if [ "$after" != "$before" ]; then
  echo "Temp leak: $before entries before, $after after."
  exit 1
fi
echo "Smoke OK: $ROUNDS rounds, billable requests 0, external requests 0, temp entries unchanged ($after)."
