#!/usr/bin/env bash
# The paid pilot (at most 10 fal requests) is an owner decision and is deliberately NOT implemented
# here: this repository has no key, no verified price and no live approval envelope. This script
# only re-runs the read-only preflight and then stops.
cd "$(dirname "$0")/.."
./scripts/live-preflight.sh || { echo "Pilot not started."; exit 2; }
echo "Preflight passed, but pilot execution is not implemented. Ask for it explicitly, with the owner present."
exit 3
