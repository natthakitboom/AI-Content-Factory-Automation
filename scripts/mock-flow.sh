#!/usr/bin/env bash
# Walks one product through the offline pipeline using only the public API: product, approved claim,
# a generated sample image, channel, campaign and a run. The worker (make dev) then plans, narrates
# (mock tone), renders and QA-checks it; the result waits for YOUR review in the dashboard.
# Asks for the owner password (hidden) or reads CF_OWNER_PASSWORD. Mock providers only.
# Usage: scripts/mock-flow.sh [api-base-url]   (default http://127.0.0.1:5080)
set -euo pipefail
API="${1:-http://127.0.0.1:5080}"
if [ -z "${CF_OWNER_PASSWORD:-}" ]; then
  if [ -t 0 ]; then
    read -r -s -p "Owner password (hidden, not stored): " CF_OWNER_PASSWORD; echo
  else
    echo "Set CF_OWNER_PASSWORD, or run this in a terminal to be asked for it." >&2; exit 2
  fi
fi
case "$API" in http://127.0.0.1:*|http://localhost:*) ;; *) echo "Only a loopback API is allowed." >&2; exit 2;; esac
command -v ffmpeg >/dev/null || { echo "ffmpeg is needed to create the sample image." >&2; exit 2; }

jar="$(mktemp)"; img="$(mktemp -d)/sample.jpg"
trap 'rm -f "$jar"; rm -rf "$(dirname "$img")"' EXIT
ffmpeg -hide_banner -loglevel error -y -f lavfi -i "testsrc2=size=540x960:rate=1:duration=1" -frames:v 1 "$img"

call() { curl -sS -b "$jar" -c "$jar" -H "X-CSRF-TOKEN: $token" "$@"; }
token="$(curl -sS -b "$jar" -c "$jar" "$API/api/v1/auth/csrf" | python3 -c 'import json,sys;print(json.load(sys.stdin)["token"])')"
body="$(printf '{"username":"%s","password":"%s"}' "${CF_OWNER_USER:-owner}" "$CF_OWNER_PASSWORD")"
code="$(call -o /dev/null -w '%{http_code}' -H 'Content-Type: application/json' -d "$body" "$API/api/v1/auth/login")"
[ "$code" = 204 ] || { echo "Sign-in failed (HTTP $code)." >&2; exit 1; }

json() { python3 -c "import json,sys;print(json.load(sys.stdin)['$1'])"; }
product="$(call -H 'Content-Type: application/json' -d '{"title":"Sample product (mock flow)","market":"TH","locale":"th-TH","category":"sample"}' "$API/api/v1/products" | json id)"
call -o /dev/null -H 'Content-Type: application/json' -d '{"text":"ช่วยให้ผิวชุ่มชื้น","sourceReference":"owner-sample-sheet","status":"approved"}' "$API/api/v1/products/$product/claims"
call -o /dev/null -F kind=image -F rightsStatus=owner_attested -F sourceReference="generated sample image" -F cloudUseAllowed=false -F "file=@$img;type=image/jpeg" "$API/api/v1/products/$product/assets"
channel="$(call -H 'Content-Type: application/json' -d '{"platform":"tiktok","displayName":"Mock flow channel","market":"TH","locale":"th-TH","timezone":"Asia/Bangkok"}' "$API/api/v1/channels" | json id)"
campaign="$(call -H 'Content-Type: application/json' -d "{\"productId\":\"$product\",\"channelId\":\"$channel\",\"mode\":\"template_only\",\"locale\":\"th-TH\",\"targetDurationMs\":25000,\"budgetUsd\":0}" "$API/api/v1/campaigns" | json id)"
run="$(call -H 'Content-Type: application/json' -H "Idempotency-Key: mock-flow-$(date +%s)-$RANDOM" -d '{"mode":"template_only","targetDurationMs":25000,"autoGenerateVoice":true,"cloudVideoSceneLimit":0}' "$API/api/v1/campaigns/$campaign/runs" | json jobId)"
echo "Run queued (job $run). Open the dashboard's Review queue in about 30 seconds and review it yourself."
