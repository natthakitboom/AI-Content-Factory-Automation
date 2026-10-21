# AI Content Factory

Local-first, single-owner pipeline that turns approved product facts and licensed media
into vertical videos with narration, captions, technical QA, human review and a manual
export package.

The implementation plan is `docs/AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md`. This is a
**mock/local-only release**: it was verified end to end with mock providers, and no paid
API has ever been called. Start with `docs/operator-runbook.md`.

## Prerequisites

- macOS arm64 with .NET 10 SDK, Node 24 LTS, Docker, FFmpeg/FFprobe
- PostgreSQL 18 in Docker on `127.0.0.1:54329`
- Optional: LM Studio on `127.0.0.1:1234`

The checked-in `global.json` pins .NET SDK `10.0.402`; `.node-version` pins Node
`24.22.0`. This checkout installed the SDK user-local at `~/.dotnet` (no sudo).

## Quick start

```bash
cp .env.example .env        # fill local-only values; never commit .env
make doctor                 # read-only; exit 2 means a prerequisite is blocked
make verify-fast            # secret scan, build, unit, integration, dashboard checks
make dev                    # PostgreSQL + API + worker + dashboard on 127.0.0.1
```

Other commands: `make verify` (every gate, including real FFmpeg and Playwright), `make backup`,
`make restore-test`, `make dev-stop` (never deletes the database), `make smoke-mock`.

## Safety defaults

```
ALLOW_PAID_APIS=false
AUTO_PUBLISH=false
PUBLISHING_MODE=manual_export
TEXT_PROVIDER=mock
VIDEO_PROVIDER=mock
ENABLE_SCHEDULES=false
```

Paid calls additionally require an owner-written live approval envelope. Mock mode must
work end to end with no API keys and no outbound AI traffic.

## Layout

```
src/ContentFactory.Contracts       shared DTOs and enums
src/ContentFactory.Core            domain services and abstractions
src/ContentFactory.Infrastructure  persistence, storage, providers, media, security
src/ContentFactory.Api             loopback REST API
src/ContentFactory.Worker          durable background worker
src/ContentFactory.Cli             owner administration commands
apps/dashboard                     Next.js owner dashboard (loopback only)
tests/                             unit, integration, media, shared test support and e2e seed
docs/                              plan, progress, decisions, runbooks
scripts/                           doctor and operational scripts
```

## Documentation

- `docs/START_HERE_DEEPSEEK.md` — how to continue development safely
- `docs/progress.md` — verified commands, evidence and blockers
- `docs/decisions.md` — recorded architecture decisions
- `docs/environment-report.md` — actual machine/toolchain findings
- `docs/operator-runbook.md` — how to run, back up, restore and release
- `docs/pilot-results.md` — measured benchmark, failure-injection coverage and the blocked live pilot
