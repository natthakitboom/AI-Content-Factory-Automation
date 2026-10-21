#!/usr/bin/env bash
# READ-ONLY readiness check before any paid cloud pilot. It changes no flag, file or row, makes
# no network call and prints no secret. Any failed check means the pilot is BLOCKED (exit 2).
set -uo pipefail
cd "$(dirname "$0")/.."
[ -f .env ] && { set -a; source .env; set +a; }

failed=0
check() { # name status detail
  printf '%-34s %-8s %s\n' "$1" "$2" "$3"
  [ "$2" = "ok" ] || failed=1
}

MODEL="${FAL_MODEL_ID:-fal-ai/wan/v2.2-a14b/image-to-video/turbo}"
[ -n "${FAL_KEY:-}" ] && check "FAL_KEY configured" ok "set (value not shown)" || check "FAL_KEY configured" BLOCKED "not set; this script never creates or requests one"
[ "${ALLOW_PAID_APIS:-false}" = "true" ] && check "ALLOW_PAID_APIS" ok "true" || check "ALLOW_PAID_APIS" BLOCKED "false; only the owner may change it inside a live envelope"
[ "${AUTO_PUBLISH:-false}" = "false" ] && check "AUTO_PUBLISH" ok "false" || check "AUTO_PUBLISH" BLOCKED "must stay false"
[ "${PUBLISHING_MODE:-manual_export}" = "manual_export" ] && check "PUBLISHING_MODE" ok "manual_export" || check "PUBLISHING_MODE" BLOCKED "must be manual_export"
[ "${VIDEO_PROVIDER:-mock}" = "fal" ] && check "VIDEO_PROVIDER" ok "fal (explicit)" || check "VIDEO_PROVIDER" BLOCKED "${VIDEO_PROVIDER:-mock}; a key alone never selects live"

if [ -f docs/provider-contracts/fal-wan-turbo.input.schema.json ]; then
  check "Input schema recorded" ok "sha256 $(shasum -a 256 docs/provider-contracts/fal-wan-turbo.input.schema.json | cut -c1-12)"
else
  check "Input schema recorded" BLOCKED "docs/provider-contracts/fal-wan-turbo.input.schema.json is missing"
fi

check "Model id" ok "$MODEL"
check "Caps configured" ok "daily ${DAILY_BUDGET_USD:-2.00} / monthly ${MONTHLY_BUDGET_USD:-30.00} / video ${MAX_VIDEO_COST_USD:-0.50} / request ${MAX_SINGLE_REQUEST_USD:-0.20} USD"

approval="${LIVE_APPROVAL_FILE:-}"
if [ -z "$approval" ] || [ ! -f "$approval" ]; then
  check "Live approval envelope" BLOCKED "no LIVE_APPROVAL_FILE; the owner must create one"
else
  verdict="$(python3 - "$approval" "$MODEL" <<'PY'
import json, sys, datetime
d = json.load(open(sys.argv[1]))
exp = datetime.datetime.fromisoformat(d["expiresAt"].replace("Z", "+00:00"))
now = datetime.datetime.now(datetime.timezone.utc)
ok = exp > now and d.get("model") == sys.argv[2] and d.get("provider") == "fal" and not d.get("allowFallbackProvider", False)
print(("ok " if ok else "BAD ") + f"expires {exp.isoformat()} count<={d.get('maxRequestCount')} total<={d.get('maxTotalUsd')} USD")
PY
)" || verdict="BAD unreadable envelope"
  case "$verdict" in ok*) check "Live approval envelope" ok "${verdict#ok }";; *) check "Live approval envelope" BLOCKED "${verdict#BAD }";; esac
fi

if docker exec content-factory-postgres true >/dev/null 2>&1; then
  q() { docker exec content-factory-postgres psql -U "${DATABASE_USER:-content_factory}" -d "${DATABASE_NAME:-content_factory}" -tAc "$1" 2>/dev/null; }
  price="$(q "select count(*) from app.price_snapshots where provider='fal' and model='$MODEL' and valid_until > now() and verified_at <= now()")"
  [ "${price:-0}" -gt 0 ] && check "Verified, unexpired price" ok "$price snapshot(s)" || check "Verified, unexpired price" BLOCKED "none recorded; the owner must record a verified price"
  rights="$(q "select count(*) from app.assets where kind='image' and cloud_use_allowed and rights_status='owner_attested'")"
  [ "${rights:-0}" -ge 5 ] && check "Images cleared for cloud use" ok "$rights images" || check "Images cleared for cloud use" BLOCKED "${rights:-0} of the 5 product images the pilot needs"
  unknown="$(q "select count(*) from app.provider_requests where status in ('submission_unknown','prepared','submitting')")"
  [ "${unknown:-1}" -eq 0 ] && check "No uncertain requests" ok "0" || check "No uncertain requests" BLOCKED "${unknown:-?} to reconcile first"
else
  check "Database checks" BLOCKED "the PostgreSQL container is not reachable, so price and rights were not checked"
fi

echo
if [ "$failed" -eq 0 ]; then echo "PREFLIGHT PASSED. This only shows readiness; it is not approval to spend."; exit 0; fi
echo "PREFLIGHT BLOCKED. Nothing was changed."; exit 2
