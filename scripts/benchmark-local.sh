#!/usr/bin/env bash
# Local benchmark: N template-only clips (default 10) through the real mock pipeline with fixed
# synthetic fixtures. Reports p50/p95 elapsed time, swap and memory-pressure readings, disk bytes,
# peak process memory, and the encoder that the real probe selected. It does not measure
# temperature (no sensor is read) and uses a mock tone, not uploaded narration.
# Usage: scripts/benchmark-local.sh [count]   -> prints a Markdown report
set -euo pipefail
COUNT="${1:-10}"
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
[ -x /opt/homebrew/opt/ffmpeg-full/bin/ffmpeg ] && export PATH="/opt/homebrew/opt/ffmpeg-full/bin:$PATH"
if [ -z "${CF_TEST_POSTGRES:-}" ] && [ -f .env.test.local ]; then export "$(cat .env.test.local)"; fi
[ -n "${CF_TEST_POSTGRES:-}" ] || { echo "CF_TEST_POSTGRES is not set; benchmark is BLOCKED (not run)." >&2; exit 2; }
command -v ffmpeg >/dev/null || { echo "ffmpeg missing; benchmark is BLOCKED (not run)." >&2; exit 2; }

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
dotnet build tests/ContentFactory.E2eSeed --nologo -v q >/dev/null
/usr/bin/time -l dotnet run --project tests/ContentFactory.E2eSeed --no-build --no-launch-profile -- bench "$work" "$COUNT" "$work/result.json" 2> "$work/time.txt" >/dev/null

python3 - "$work/result.json" "$work/time.txt" <<'PY'
import json, re, statistics, sys
r = json.load(open(sys.argv[1]))
t = open(sys.argv[2]).read()
s = sorted(r["samplesMs"])
def pct(p):
    k = (len(s) - 1) * p
    lo, hi = int(k), min(int(k) + 1, len(s) - 1)
    return s[lo] + (s[hi] - s[lo]) * (k - lo)
def grab(label):
    m = re.search(r"(\d+(?:\.\d+)?)\s+" + label, t)
    return float(m.group(1)) if m else None
rss = grab("maximum resident set size")
print(f"## Local benchmark ({r['date'][:10]})")
print()
print(f"- Samples: {r['count']} template-only clips, {r['failures']} failed (fixed synthetic fixtures, mock tone voice)")
print(f"- Elapsed per clip: p50 {pct(.5)/1000:.1f} s, p95 {pct(.95)/1000:.1f} s, min {s[0]/1000:.1f} s, max {s[-1]/1000:.1f} s; first (cold) {r['samplesMs'][0]/1000:.1f} s")
print(f"- Encoder: {r['encoder']} (hardware: {r['hardwareAccelerated']}) — {r['encoderReason']}")
print(f"- FFmpeg: {r['ffmpeg']}")
print(f"- Host: {r['host']['model']}, {r['host']['cpus']} CPUs, {int(r['host']['memBytes'])/2**30:.0f} GiB RAM")
print(f"- Swap before: {r['swapBefore']} | after: {r['swapAfter']}")
print(f"- Memory pressure before: {r['pressureBefore']} | after: {r['pressureAfter']}")
print(f"- Output on disk: {r['outputBytes']/2**20:.1f} MiB for {r['count']} clips")
peak = r["peakWorkingSetBytes"]
print("- Peak memory of the benchmark process: " + (f"{peak/2**20:.0f} MiB" if peak > 0 else "unavailable on this OS") + "; largest single process in the tree (time -l): " + (f"{rss/2**20:.0f} MiB" if rss else "unavailable"))
print(f"- Temperature: {r['temperature']}")
print()
print("This is a local measurement of one machine on one day, not a model comparison or a cost figure.")
PY
