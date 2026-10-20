# Environment report

- Captured: 2026-10-20T14:42:15Z (Asia/Bangkok 21:42)
- Host: macOS 26.7.1, build 25G241, arm64
- Memory: 128.0 GiB
- Free disk: 1532.3 GiB (82.5% of the checked volume)
- No serial number, hostname or full username path is recorded here.

## Toolchain

| Check | Result | Notes |
|---|---|---|
| .NET SDK | OK — 10.0.402 | Installed user-local at `~/.dotnet` (no sudo). Pinned in `global.json`; `make` prepends it to `PATH`. |
| Node | FAIL — v26.11.1 installed, pin is 24.22.0 | Dashboard work must use the pinned Node 24 LTS. No Node work is required for M0/T00–T05. |
| Docker engine | OK — 29.8.2 | Reachable. No container was created during bootstrap. |
| FFmpeg | FAIL — not installed | Native media gates are BLOCKED, not passed. |
| FFprobe | FAIL — not installed | Same; no media test was claimed as passing. |
| PostgreSQL client (`psql`) | not installed | PostgreSQL 18 will run in Docker; the client is optional. |
| git | 2.54.0 | User identity already configured; no identity was invented. |

## Ports

| Port | Purpose | State |
|---:|---|---|
| 3000 | dashboard | IN USE by an unrelated process at check time (not killed) |
| 5080 | API | free |
| 54329 | PostgreSQL (compose) | free |

## Optional runtime AI

- LM Studio was not reachable on `127.0.0.1:1234` at check time.
- Mock providers are the only runtime mode until a separate live approval exists.

## Doctor result

Command: `./scripts/doctor.sh`
Exit code: `2` (BLOCKED)

Failures were: Node major mismatch (26 vs pinned 24), missing FFmpeg, missing FFprobe,
and port 3000 already in use. Warnings: `DATA_ROOT` unset (default outside the repo) and
LM Studio unreachable.

Implication: unit tests and (after T01) PostgreSQL integration tests can run. Media
render/QA tests and any live-provider work are blocked until FFmpeg is installed and the
pinned Node LTS is available. No software was installed with sudo and no power/firewall
settings were changed.
