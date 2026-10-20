# Development progress

## Current milestone
M0 — T00–T05

## Task status
| Task | Status | Commit | Evidence | Blocker |
|---|---|---|---|---|
| T00 | done | pending | `dotnet test tests/ContentFactory.UnitTests --filter RepositoryBootstrapTests` → 7 passed; `scripts/doctor.sh` → exit 2 | Node 24 pin not installed, FFmpeg/FFprobe missing, port 3000 busy (media/live gates blocked) |
| T01 | not_started | - | - | - |
| T02 | not_started | - | - | - |
| T03 | not_started | - | - | - |
| T04 | not_started | - | - | - |
| T05 | not_started | - | - | - |

## Last verified commands

### T00
- Command: `dotnet test tests/ContentFactory.UnitTests --filter RepositoryBootstrapTests`
  - RED (before implementation): total 7, passed 1, failed 6 (missing `.env.example`,
    `global.json`, `.node-version`, `Directory.Packages.props`, `.gitignore`, doctor).
  - GREEN: total 7, passed 7, failed 0, skipped 0.
  - Environment: macOS arm64, .NET SDK 10.0.402 (user-local), 2026-10-20.
- Command: `./scripts/doctor.sh`
  - Exit code: 2 (BLOCKED)
  - Failures: Node 26.11.1 vs pinned 24.22.0; FFmpeg missing; FFprobe missing;
    port 3000 in use.
  - Warnings: `DATA_ROOT` unset; LM Studio unreachable.
  - Environment: 2026-10-20T14:42:15Z.

## Files changed

### T00
Created: `.env.example`, `.node-version`, `global.json`, `Directory.Build.props`,
`Directory.Packages.props`, `ContentFactory.slnx`, `.gitignore`, `Makefile`,
`scripts/doctor.sh`, `AGENTS.md`, `README.md`, `docs/START_HERE_DEEPSEEK.md`,
`docs/progress.md`, `docs/decisions.md`, `docs/environment-report.md`,
`tests/ContentFactory.UnitTests/RepositoryBootstrapTests.cs`, plus the project skeleton
under `src/` and `tests/` (Contracts, Core, Infrastructure, Api, Worker, Cli,
UnitTests, IntegrationTests, MediaTests).

Notes: `AGENTS.md` and `docs/START_HERE_DEEPSEEK.md` did not exist in the handed-over
checkout; they were created from sections F1/I1 of the plan. The plan file remains at
the repository root at its original path (`AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md`);
it was not moved or rewritten.

## Architecture decisions changed
- ADR-0001: .NET 10.0.402 installed user-local at `~/.dotnet`; `global.json` pins it.
- ADR-0002: central package management with exact versions.
- ADR-0003: integration tests require real PostgreSQL via `CF_TEST_POSTGRES`.
- ADR-0004: content-addressed assets; rights stored separately.
- ADR-0005: `decimal`/`numeric(18,6)` money and `timestamptz` instants.
- No plan-level architecture change.

## Paid API activity
- Disabled; no paid requests authorized and none made.

## Next exact task
- Task ID: T01 — Shared contracts and PostgreSQL schema
- Inputs/files to read: plan C1–C4, T01, `docs/decisions.md`
- First failing tests: `SchemaTests` — `DuplicateDedupeKeyRejected`,
  `MoneyRoundTripsSixDecimals`, `InvalidCommissionRateRejected`,
  `TimestampRetainsInstant`
- Prerequisite: PostgreSQL reachable on `127.0.0.1:54329`; `CF_TEST_POSTGRES` set.
