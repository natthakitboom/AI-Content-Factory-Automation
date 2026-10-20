# Development progress

## Current milestone
M0 — T00–T05

## Task status
| Task | Status | Commit | Evidence | Blocker |
|---|---|---|---|---|
| T00 | done | 1ee6bfa | `dotnet test tests/ContentFactory.UnitTests --filter RepositoryBootstrapTests` → 7 passed; `scripts/doctor.sh` → exit 2 | Node 24 pin not installed, FFmpeg/FFprobe missing, port 3000 busy (media/live gates blocked) |
| T01 | done | pending | `make test-unit` → 9 passed; `make test-integration` → 6 passed; `make migrate` twice → second run no-op | PostgreSQL in Docker on loopback; test DBs are `cf_test_<runid>` and dropped |
| T02 | done | pending | `make test-unit` → 11 passed; `make test-integration` → 15 passed (8 auth + 1 CLI + 6 schema) | Dashboard proxy not built in M0; deferred with UI tasks |
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

### T01
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter SchemaTests`
  - RED (before migration): total 5, failed 5. PostgreSQL error `42P01` (missing
    `app.owner_users` / `app.jobs`), i.e. the failure was the absent schema, not the
    environment.
  - GREEN: total 6, passed 6, failed 0, skipped 0 (includes migration re-run).
  - Environment: PostgreSQL 18 in Docker at `127.0.0.1:54329`, test DB `cf_test_<runid>`.
- Command: `make test-unit` → 9 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 6 passed, 0 failed, 0 skipped.
- Command: `make migrate` (new app DB) → applied `InitialSchema`, done.
- Command: `make migrate` (already migrated) → "No migrations were applied. The
  database is already up to date." Done.
- Command: `dotnet ef migrations has-pending-model-changes` → "No changes have been
  made to the model since the last migration."

### T02
- Command: `make test-integration` (before implementation)
  - RED: 8 authentication tests failed because the API had no DbContext/auth endpoints;
    the 6 schema tests still passed. Failure was missing implementation, not environment.
  - GREEN: 15 passed, 0 failed, 0 skipped (8 auth, 1 CLI, 6 schema).
- Command: `make test-unit` → 11 passed, 0 failed, 0 skipped (2 new secret-redactor tests).
- Notes: no interactive `make owner-init` run was claimed. The CLI hashing path is
  exercised by `InitOwnerCommandTests` against the test database.

## Files changed

### T02
Created: `src/ContentFactory.Infrastructure/Security/SecretRedactor.cs`,
`Security/LoginThrottle.cs`, `Persistence/DatabaseConnection.cs`,
`src/ContentFactory.Api/Middleware/LocalRequestGuard.cs`,
`Middleware/CsrfMiddleware.cs`, `Middleware/RequestLoggingMiddleware.cs`,
`Middleware/LocalBindingGuard.cs`, `Middleware/ApiProblem.cs`,
`src/ContentFactory.Api/Endpoints/AuthEndpoints.cs`,
`src/ContentFactory.Cli/Commands/InitOwnerCommand.cs`,
`tests/ContentFactory.IntegrationTests/Api/CapturingLoggerProvider.cs`,
`Api/ApiFactory.cs`, `Api/ApiTestFixture.cs`, `AuthenticationTests.cs`,
`InitOwnerCommandTests.cs`, `tests/ContentFactory.UnitTests/SecretRedactorTests.cs`.
Modified: `src/ContentFactory.Api/Program.cs`, `src/ContentFactory.Cli/Program.cs`,
`src/ContentFactory.Cli/ContentFactory.Cli.csproj` (ASP.NET shared framework for
PasswordHasher), integration test project reference, `Makefile` (`owner-init`).

### T01
Created: `src/ContentFactory.Contracts/*` (C4 DTOs, enums and `JsonDefaults`),
`src/ContentFactory.Core/Abstractions/*` (C4 interfaces),
`src/ContentFactory.Infrastructure/Persistence/AppDbContext.cs`,
`Persistence/Entities/*`, `Persistence/DesignTimeDbContextFactory.cs`,
`Persistence/Migrations/20261020144646_InitialSchema*`, `infra/compose.yaml`,
`infra/postgres/init-test-db.sql`, `.config/dotnet-tools.json`, `.env.test.example`,
`tests/ContentFactory.UnitTests/ContractSerializationTests.cs`,
`tests/ContentFactory.IntegrationTests/Infrastructure/PostgresTestDatabase.cs`,
`tests/ContentFactory.IntegrationTests/Infrastructure/PostgresSchemaFixture.cs`,
`tests/ContentFactory.IntegrationTests/SchemaTests.cs`.
Modified: `Directory.Packages.props` (transitive pinning), project files (EF/Npgsql
references), `Makefile` (infra-up/down, migrate, dotnet path), `docs/decisions.md`.

Notes: the compose volume mounts `/var/lib/postgresql` because the PostgreSQL 18 image
rejects a mount at `/var/lib/postgresql/data`; this was an infrastructure fix, not a
schema change. `DateTimeOffset` values are normalised to UTC at the EF boundary because
Npgsql accepts only UTC offsets for `timestamptz`.

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
- Task ID: T03 — Asset ingest, rights and content-addressed storage
- Inputs/files to read: plan E1, C2 assets, C4 IAssetStore, T03
- First failing tests: `AssetPathTests` and `AssetIngestTests` —
  `TraversalAndSymlinkEscapeRejected`, `ThaiFilenameDoesNotBecomeDiskPath`,
  `SvgDisguisedAsJpegRejected`, `OversizedUploadRejected`,
  `RightsRequiredForProduction`
- Prerequisite: T01/T02; PostgreSQL reachable; no FFmpeg needed.
