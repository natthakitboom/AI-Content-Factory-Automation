#!/usr/bin/env bash
# Read-only prerequisite check. Installs nothing, starts nothing, calls no paid API,
# and never prints serial numbers or full home-directory paths.
#
# Exit codes:
#   0  all required prerequisites for the current milestone are present
#   2  at least one required prerequisite is missing
#   1  internal doctor error
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

FAIL=0
WARN=0

trap 'echo "[fail] doctor internal error at line $LINENO" >&2; exit 1' ERR

ok()   { printf '[ok]   %s\n' "$1"; }
warn() { printf '[warn] %s\n' "$1"; WARN=$((WARN + 1)); }
fail() { printf '[fail] %s\n' "$1"; FAIL=$((FAIL + 1)); }

section() { printf '\n== %s ==\n' "$1"; }

printf '# Content Factory doctor\n'
printf 'timestamp_utc: %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
printf 'arch: %s\n' "$(uname -m)"
printf 'os: %s %s (build %s)\n' \
  "$(sw_vers -productName)" "$(sw_vers -productVersion)" "$(sw_vers -buildVersion)"

if [ "$(uname -s)" != "Darwin" ]; then
  warn "this project targets macOS; detected $(uname -s)"
fi

mem_bytes="$(sysctl -n hw.memsize 2>/dev/null || echo 0)"
if [ "${mem_bytes:-0}" -gt 0 ] 2>/dev/null; then
  printf 'memory_gib: %s\n' "$(awk -v b="$mem_bytes" 'BEGIN { printf "%.1f", b / 1024 / 1024 / 1024 }')"
else
  warn "could not read hw.memsize"
fi

section "Toolchain"

DOTNET_BIN="$(command -v dotnet || true)"
if [ -z "$DOTNET_BIN" ]; then
  fail "dotnet not found on PATH (install .NET 10 SDK or add it to PATH)"
else
  DOTNET_INFO="$(dotnet --info 2>/dev/null || true)"
  DOTNET_SDKS="$(dotnet --list-sdks 2>/dev/null || true)"
  DOTNET_VER="$(dotnet --version 2>/dev/null || true)"
  if [ -z "$DOTNET_INFO" ]; then
    fail "dotnet --info failed; the SDK installation is incomplete"
  fi
  if printf '%s\n' "$DOTNET_SDKS" | grep -q '^10\.'; then
    ok "dotnet SDK $(printf '%s\n' "$DOTNET_SDKS" | grep '^10\.' | head -1 | sed "s|$HOME|~|g")"
  else
    fail "a .NET 10 SDK was not found (dotnet --version: ${DOTNET_VER:-unknown})"
  fi
  if [ "$DOTNET_BIN" = "$HOME/.dotnet/dotnet" ]; then
    ok "using user-local SDK at ~/.dotnet (no sudo)"
  fi
fi

if command -v node >/dev/null 2>&1; then
  NODE_VER="$(node --version 2>/dev/null | tr -d 'v')"
  NODE_MAJOR="${NODE_VER%%.*}"
  PINNED_NODE="$(tr -d '[:space:]' < .node-version 2>/dev/null || true)"
  if [ "$NODE_MAJOR" = "${PINNED_NODE%%.*}" ]; then
    ok "node $NODE_VER (pinned $PINNED_NODE)"
  else
    fail "node $NODE_VER does not match pinned $PINNED_NODE (use the pinned LTS for dashboard work)"
  fi
else
  fail "node not found on PATH (pinned ${PINNED_NODE:-24.x})"
fi

if command -v docker >/dev/null 2>&1; then
  DOCKER_SERVER="$(docker version --format '{{.Server.Version}}' 2>/dev/null || true)"
  if [ -n "$DOCKER_SERVER" ]; then
    ok "docker engine $DOCKER_SERVER"
  else
    fail "docker CLI exists but the engine is not reachable"
  fi
else
  fail "docker not found on PATH"
fi

if ffmpeg -version >/dev/null 2>&1; then
  ok "$(ffmpeg -version 2>/dev/null | head -1)"
else
  fail "ffmpeg not found — native media gates are BLOCKED (not passed)"
fi

if ffprobe -version >/dev/null 2>&1; then
  ok "$(ffprobe -version 2>/dev/null | head -1)"
else
  fail "ffprobe not found — native media gates are BLOCKED (not passed)"
fi

section "Storage"

DISK_KB="$(df -Pk "$ROOT" | awk 'NR==2 {print $4}')"
DISK_TOTAL_KB="$(df -Pk "$ROOT" | awk 'NR==2 {print $2}')"
if [ -n "${DISK_KB:-}" ] && [ "${DISK_TOTAL_KB:-0}" -gt 0 ] 2>/dev/null; then
  FREE_GIB="$(awk -v k="$DISK_KB" 'BEGIN { printf "%.1f", k / 1024 / 1024 }')"
  FREE_RATIO="$(awk -v f="$DISK_KB" -v t="$DISK_TOTAL_KB" 'BEGIN { printf "%.3f", f / t }')"
  MIN_GIB="${MIN_FREE_DISK_GIB:-20}"
  MIN_RATIO="${MIN_FREE_DISK_RATIO:-0.10}"
  if awk -v f="$FREE_GIB" -v m="$MIN_GIB" 'BEGIN { exit !(f >= m) }' \
     && awk -v f="$FREE_RATIO" -v m="$MIN_RATIO" 'BEGIN { exit !(f >= m) }'; then
    ok "free disk ${FREE_GIB} GiB (ratio ${FREE_RATIO})"
  else
    fail "free disk ${FREE_GIB} GiB / ratio ${FREE_RATIO} is below reserve ${MIN_GIB} GiB / ${MIN_RATIO}"
  fi
else
  warn "could not read free disk space"
fi

DATA_ROOT_DEFAULT="$HOME/Library/Application Support/ContentFactory"
DATA_ROOT_CHECK="${DATA_ROOT:-$DATA_ROOT_DEFAULT}"
if [ -z "${DATA_ROOT:-}" ]; then
  warn "DATA_ROOT is not set; the default location outside the repo will be used"
fi
if [ -d "$DATA_ROOT_CHECK" ] && [ -w "$DATA_ROOT_CHECK" ]; then
  ok "DATA_ROOT is a writable directory (path intentionally not printed)"
elif [ -w "$(dirname "$DATA_ROOT_CHECK")" ]; then
  ok "DATA_ROOT parent is writable; directory will be created on first use"
else
  fail "DATA_ROOT is not writable"
fi

section "Ports"

check_port() {
  local port="$1" label="$2"
  if command -v lsof >/dev/null 2>&1 && lsof -nP -iTCP:"$port" -sTCP:LISTEN >/dev/null 2>&1; then
    fail "port $port ($label) is already in use"
  else
    ok "port $port ($label) is free"
  fi
}
check_port "${DASHBOARD_PORT:-3000}" "dashboard"
check_port 5080 "api"
check_port "${DATABASE_PORT:-54329}" "postgres"

section "Optional runtime AI"

if curl -sS --max-time 2 -o /dev/null "http://127.0.0.1:1234/v1/models" 2>/dev/null; then
  ok "LM Studio is reachable on 127.0.0.1:1234"
else
  warn "LM Studio is not reachable on 127.0.0.1:1234 (mock text mode still works)"
fi

section "Summary"
printf 'failures: %d  warnings: %d\n' "$FAIL" "$WARN"
if [ "$FAIL" -gt 0 ]; then
  printf 'result: BLOCKED — resolve failures before media/live gates\n'
  exit 2
fi

printf 'result: READY for the current milestone\n'
exit 0
