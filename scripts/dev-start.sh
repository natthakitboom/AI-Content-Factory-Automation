#!/usr/bin/env bash
# Starts the local stack on loopback only: PostgreSQL (Docker, kept between runs), the API, the
# worker and the dashboard. Providers stay whatever .env says (mock by default). Nothing here
# enables paid work, and nothing is installed or registered to start at login.
set -euo pipefail
cd "$(dirname "$0")/.."
export PATH="$HOME/.dotnet:$PATH"
[ -x "$HOME/.node/v$(cat .node-version)/bin/node" ] && export PATH="$HOME/.node/v$(cat .node-version)/bin:$PATH"
[ -x /opt/homebrew/opt/ffmpeg-full/bin/ffmpeg ] && export PATH="/opt/homebrew/opt/ffmpeg-full/bin:$PATH"
[ -f .env ] || { echo ".env is missing: run 'cp .env.example .env' and set DATABASE_PASSWORD." >&2; exit 2; }
set -a; source .env; set +a

ROOT="$(pwd)"
mkdir -p "$ROOT/.run"
docker compose -f infra/compose.yaml --env-file .env up -d postgres
echo -n "Waiting for PostgreSQL"
for _ in $(seq 1 60); do
  [ "$(docker inspect -f '{{.State.Health.Status}}' content-factory-postgres 2>/dev/null)" = healthy ] && break
  echo -n "."; sleep 1
done
echo
make migrate >/dev/null

start() { # name logfile command...
  local name="$1" log="$2"; shift 2
  if [ -f "$ROOT/.run/$name.pid" ] && kill -0 "$(cat "$ROOT/.run/$name.pid")" 2>/dev/null; then
    echo "$name is already running (pid $(cat "$ROOT/.run/$name.pid"))."; return
  fi
  nohup "$@" > "$ROOT/$log" 2>&1 &
  echo $! > "$ROOT/.run/$name.pid"; echo "$name started (pid $!, log $log)."
}

dotnet build ContentFactory.slnx --nologo -v q >/dev/null
export ASPNETCORE_URLS="http://127.0.0.1:5080"
start api .run/api.log dotnet run --project src/ContentFactory.Api --no-build --no-launch-profile
start worker .run/worker.log dotnet run --project src/ContentFactory.Worker --no-build --no-launch-profile
if [ ! -d apps/dashboard/.next ]; then (cd apps/dashboard && npm ci --no-audit --no-fund >/dev/null && API_ORIGIN="http://127.0.0.1:5080" npm run build >/dev/null); fi
(cd apps/dashboard && API_ORIGIN="http://127.0.0.1:5080" start dashboard .run/dashboard.log npm run start)

echo
echo "Dashboard: http://127.0.0.1:3000   API: http://127.0.0.1:5080   (providers: text=${TEXT_PROVIDER:-mock} video=${VIDEO_PROVIDER:-mock})"
echo "Create the owner once with: dotnet run --project src/ContentFactory.Cli -- init-owner owner"
echo "Stop with: make dev-stop"
