# Start here — DeepSeek / OpenCode

This repository implements the plan in `docs/AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md`.
It is not a finished product. Development proceeds milestone by milestone with
test evidence.

## Read first

1. `AGENTS.md`
2. This file
3. `docs/progress.md`
4. The task you are about to implement in the plan (section G) plus the contracts it
   depends on (sections C–F)

## Current scope

- M0 (T00–T05) is complete and was reviewed by the owner.
- Milestone M1: T06–T12 (durable jobs, worker orchestration, local script planning,
  budget/approval controls, narration, timeline/render, technical QA and review).
- T00–T13 are complete. T14 is next.
- Do not start M2+ work until the owner reviews M1.

## Safety rails

- `ALLOW_PAID_APIS=false`, `AUTO_PUBLISH=false`, `PUBLISHING_MODE=manual_export`.
- Mock providers only. No real API keys, no paid calls, no accounts, no credits.
- Loopback-only endpoints. No public tunnels or wildcard CORS.
- No Redis/MinIO/n8n/Kubernetes/vector DB additions.
- Do not install system software with sudo or change firewall/power settings.
- Never commit secrets, `.env`, generated media or personal data.

## Local environment

| Item | Value |
|---|---|
| OS | macOS 26.7.1 arm64 (128 GiB) |
| .NET SDK | 10.0.402, user-local at `~/.dotnet` |
| Node pin | 24.22.0 (`.node-version`) |
| PostgreSQL | Docker, `127.0.0.1:54329` |
| API | `http://127.0.0.1:5080` |
| Dashboard | `http://127.0.0.1:3000` |
| FFmpeg | not installed at bootstrap → media gates blocked |
| LM Studio | optional at `http://127.0.0.1:1234/v1` |

`make` prepends `~/.dotnet` to `PATH` so the pinned SDK is used.

## Commands

```bash
make doctor            # read-only prerequisite check; exit 2 when blocked
make build
make test-unit
make test-integration  # requires CF_TEST_POSTGRES
make test-media        # requires ffmpeg/ffprobe
make verify
```

Integration tests use a real PostgreSQL database. Export an explicit maintenance
connection string, for example:

```bash
export CF_TEST_POSTGRES='Host=127.0.0.1;Port=54329;Database=postgres;Username=content_factory;Password=<local-only>'
```

The test fixture creates and drops databases named `cf_test_<runid>` only. The app
database `content_factory` must never be used by tests.

## Evidence policy

- Record actual commands, exit codes and pass/fail/skip counts in `docs/progress.md`.
- Never write PASS for a command that was not run.
- A skipped media/live test is `blocked`, not passed.
- Report known limitations and blockers honestly.

## Task order

T00 → T01 → T02 → T03 → T04 → T05 → M0 review → T06 → T07 → T08 → T09 → T10 →
T11 → T12 → M1 review.

Each task follows RED → GREEN → review → progress update → commit. The exact test names
and assertions are listed in the plan; implement those plus adjacent edge cases.
