# AI Content Factory

Local-first, single-owner pipeline that turns approved product facts and licensed media
into vertical videos with narration, captions, technical QA, human review and a manual
export package.

The implementation plan is `docs/AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md`. The current
milestone is M0 (T00–T05). Development uses mock providers and never calls a paid API.

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
make build
make test-unit
```

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
tests/                             unit, integration and media tests
docs/                              plan, progress, decisions, runbooks
scripts/                           doctor and operational scripts
```

## Documentation

- `docs/START_HERE_DEEPSEEK.md` — how to continue development safely
- `docs/progress.md` — verified commands, evidence and blockers
- `docs/decisions.md` — recorded architecture decisions
- `docs/environment-report.md` — actual machine/toolchain findings
