# Development progress

## Current milestone
M1 — T06–T12 (T10 complete; T11 next)

## Task status
| Task | Status | Commit | Evidence | Blocker |
|---|---|---|---|---|
| T00 | done | 1ee6bfa | `dotnet test tests/ContentFactory.UnitTests --filter RepositoryBootstrapTests` → 7 passed; `scripts/doctor.sh` → exit 2 | Node 24 pin not installed, FFmpeg/FFprobe missing, port 3000 busy (media/live gates blocked) |
| T01 | done | 9fb596d | `make test-unit` → 9 passed; `make test-integration` → 6 passed; `make migrate` twice → second run no-op | PostgreSQL in Docker on loopback; test DBs are `cf_test_<runid>` and dropped |
| T02 | done | d29c3b1 | `make test-unit` → 11 passed; `make test-integration` → 15 passed (8 auth + 1 CLI + 6 schema) | Dashboard proxy not built in M0; deferred with UI tasks |
| T03 | done | 3363b61 | `make build` → 0 warnings/0 errors; `make test-unit` → 18 passed; `make test-integration` → 23 passed; filters `AssetPathTests` → 7, `AssetIngestTests` → 8 | None for T03; media/live gates remain blocked (FFmpeg, LM Studio, Node 24) |
| T04 | done | 52c61eb | `make build` → 0 warnings/0 errors; `make test-unit` → 31 passed; filter `SafeProcessTests|ManifestHashTests` → 13 passed; `make test-integration` → 23 passed | No FFmpeg needed; media/live gates remain blocked |
| T05 | done | 5b8c780 | `make build` → 0 warnings/0 errors; `make test-unit` → 31 passed; filtered `CampaignRunTests` → 5 passed; `make test-integration` → 28 passed | M0 gate reached; media/live gates still blocked (FFmpeg, LM Studio, Node 24) |
| T06 | done | d68ad34 | `make build` → 0 warnings/0 errors; `make test-unit` → 31 passed; filtered `JobLeasingTests` → 7 passed; `make test-integration` → 35 passed; 100-job two-worker finalization test passed | No FFmpeg/LM Studio needed; provider uncertainty is blocked for reconciliation, not retried |
| T07 | done | 16c1d6f | `make build` → 0 warnings/0 errors; `make test-unit` → 37 passed; filtered `WorkflowTests` → 6 passed; `make test-integration` → 40 passed; filtered `WorkerRecoveryTests` → 5 passed; no pending model changes | No FFmpeg/LM Studio needed; no provider calls; pause does not cancel running cloud work |
| T08 | done | 0a1ee43, 9d3616f | `make build` → 0 warnings/0 errors; `make test-unit` → 47 passed; filtered `ScriptValidationTests` → 10 passed; `make test-integration` → 56 passed; filtered `PlannerAdapterTests` → 16 passed; live LM Studio smoke → 2/2 validator-approved runs; no pending model changes | Live LM Studio remains an optional local gate (not CI); no paid calls; token usage is captured in `ScriptPlanTrace` because the plan schema has no token column |
| T09 | done | 115360a | `make build` → 0 warnings/0 errors; `make test-unit` → 56 passed; filtered `LiveApprovalTests` → 9 passed; `make test-integration` → 71 passed; filtered `BudgetRaceTests` → 15 passed; no pending model changes; `make migrate` applied `AddBudgetReservations` | No paid calls; FFmpeg/Node/port 3000 blockers unchanged; T13 must use the video-scope overload for a per-video cap |
| T10 | done | 6bd8282 | `make build` → 0 warnings/0 errors; `make test-unit` → 73 passed; `make test-integration` → 82 passed; filtered `VoiceManifestTests` → 17 passed; filtered `VoiceProviderTests` → 11 passed; no pending model changes; `make test-media` → exit 2 blocked | No live Azure/paid calls; real FFprobe/FFmpeg execution blocked because the binaries are missing; adapter is not yet wired to a worker stage (T12) |

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

### T03
- Command: `make test-unit` (before implementation)
  - RED: build failed with `CS0103: The name 'AssetUploadPolicy' does not exist` at
    `AssetPathTests.cs:131` and `:132`, i.e. the test contract existed but the upload
    guard did not.
- Command: `make test-integration` (before implementation)
  - RED: build failed with `CS1061: ApiTestFixture does not contain DataRoot` and
    `CS0246: FileSystemAssetStore could not be found`, i.e. the API/fixture surface for
    asset ingest was absent.
- Command: `make test-unit` → 18 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 23 passed, 0 failed, 0 skipped
  (8 asset ingest, 8 auth, 1 CLI, 6 schema).
- Command: `dotnet test tests/ContentFactory.UnitTests --filter "FullyQualifiedName~AssetPathTests"`
  → 7 passed, 0 failed, 0 skipped.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter "FullyQualifiedName~AssetIngestTests"`
  → 8 passed, 0 failed, 0 skipped (with `CF_TEST_POSTGRES` from `.env.test.local`).
- Command: `make build` → 0 warnings, 0 errors.
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases, temporary per-fixture `DATA_ROOT`.
- Notes: no paid API, cloud upload or network AI call was made. The content endpoint is
  authenticated and has no public URL. Rights are metadata, not part of the hash.

### T04
- Command: `make test-unit` (before implementation)
  - RED: build failed with `CS0234: The type or namespace name 'Media' does not exist in
    the namespace 'ContentFactory.Infrastructure'` and `CS0246: SafeProcessRunner could
    not be found`, i.e. the test contracts existed but the implementations did not.
- Command: `dotnet test tests/ContentFactory.UnitTests --filter
  "FullyQualifiedName~SafeProcessTests|FullyQualifiedName~ManifestHashTests"`
  → 13 passed, 0 failed, 0 skipped (7 process, 6 manifest).
- Command: `make test-unit` → 31 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 23 passed, 0 failed, 0 skipped (unchanged; T04 has
  no database surface).
- Command: `make build` → 0 warnings, 0 errors.
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local). The tests use `/usr/bin/printf`,
  `/bin/sh`, `/bin/sleep`, `/usr/bin/yes` and `/usr/bin/env`; FFmpeg was not required.
- Notes: `CancellationStopsChildProcess` uses a shell loop that appends to a marker file;
  after cancellation the marker stops growing, proving the process tree was stopped.
  `SecretEnvironmentIsNotPassedToChild` sets a parent secret and confirms `/usr/bin/env`
  cannot see it. `OutputIsCapped` proves the cap without deadlocking a noisy child.
  No paid API, cloud upload or network AI call was made.

### T05
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter CampaignRunTests`
  (before implementation)
  - RED: total 4, passed 0, failed 4. Every test failed at the first API call with
    `Expected: Created, Actual: NotFound` because the product/campaign endpoints did not
    exist. The failures were missing implementation, not a database or environment fault.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter CampaignRunTests`
  → 5 passed, 0 failed, 0 skipped (`DuplicateRequestReturnsSameJob`,
  `SameKeyDifferentBodyReturns409`, `UnapprovedClaimsBlockRun`,
  `ProductEditDoesNotMutateOldRevision`, `UnattestedAssetBlocksRun`).
- Command: `make test-unit` → 31 passed, 0 failed, 0 skipped (unchanged; T05 is an API
  and PostgreSQL surface).
- Command: `make test-integration` → 28 passed, 0 failed, 0 skipped (5 campaign run,
  8 asset ingest, 8 auth, 1 CLI, 6 schema).
- Command: `make build` → 0 warnings, 0 errors.
- API smoke: `CampaignRunTests` drives the real in-process API pipeline with cookie auth,
  CSRF and PostgreSQL. It creates a product, an approved claim, a channel, a campaign and
  a run, then reloads the persisted revision. No FFmpeg, LM Studio, paid API or other
  network AI call is involved.
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases, temporary per-fixture `DATA_ROOT`.
  Verified 2026-10-20T15:15:40Z.
- Notes: the snapshot is rebuilt only from database rows. The request body includes
  deliberately spoofed `productSnapshot` and `price` fields in the duplicate test; they
  are ignored. Pending claims are excluded and a product with no approved, unexpired
  claim is blocked with `RUN_FACTS_UNAPPROVED`. Attached assets with unknown/rejected
  rights block the run with `RUN_ASSET_RIGHTS_UNATTESTED`. The same idempotency key with
  the same canonical body returns the original job and revision; a different body returns
  `409 IDEMPOTENCY_CONFLICT` and creates nothing.

### T05
Created: `src/ContentFactory.Core/Products/ProductSnapshotBuilder.cs`,
`src/ContentFactory.Core/Exceptions/ConflictException.cs`,
`src/ContentFactory.Infrastructure/Jobs/RunEnqueuer.cs`,
`src/ContentFactory.Api/Endpoints/ProductEndpoints.cs`,
`src/ContentFactory.Api/Endpoints/CampaignEndpoints.cs`,
`tests/ContentFactory.IntegrationTests/CampaignRunTests.cs`.
Modified: `src/ContentFactory.Api/Program.cs` (run enqueuer DI and endpoint mapping),
`src/ContentFactory.Api/Middleware/ExceptionMappingMiddleware.cs` (409 conflict mapping),
`docs/decisions.md` (ADR-0008), `docs/progress.md`.

Notes: `POST /api/v1/products`, `POST /api/v1/products/{id}/claims`,
`POST /api/v1/products/{id}/offers`, `GET /api/v1/products/{id}`,
`POST /api/v1/campaigns` and `POST /api/v1/campaigns/{id}/runs` require authentication;
mutations still require CSRF. A run inserts exactly one `content_revisions` row and one
`plan_script` job in the same transaction. Revision numbering takes a `FOR UPDATE` lock on
the campaign row. The idempotency key (16–128 printable ASCII) and campaign id form the
job `dedupe_key`; the canonical request hash and revision id live in the job payload.
Cloud scene limits above zero are rejected while paid work is disabled. No client-supplied
snapshot, claim text or price is accepted as fact.

### T06
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter JobLeasingTests`
  (before implementation)
  - RED: build failed with `CS0246: The type or namespace name 'IOutboxHandler'`,
    `OutboxMessage` and `NewJob` could not be found. The test contracts existed but the
    durable job store, watchdog and outbox dispatcher did not.
- Command: `make infra-up`
  - The PostgreSQL container had stopped (`Connection refused` on `127.0.0.1:54329`).
    It was started and reported healthy. This was an environment prerequisite, not a code
    failure. Every test run still creates and drops its own `cf_test_<runid>` database.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter JobLeasingTests`
  → 7 passed, 0 failed, 0 skipped:
  `TwoWorkersNeverOwnSameFence`, `ExpiredWorkerCannotFinalize`,
  `UnknownProviderSubmissionBlocksRequeue`, `RetryWaitRespectsNextRunAt`,
  `HeartbeatExtendsLease`, `OutboxReplayCreatesOneNextJob`,
  `RollbackCreatesNeitherRevisionNorJob`.
- Command: `make test-unit` → 31 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 35 passed, 0 failed, 0 skipped (7 job leasing,
  5 campaign run, 8 asset ingest, 8 auth, 1 CLI, 6 schema).
- Command: `make build` → 0 warnings, 0 errors.
- Command: `dotnet ef migrations has-pending-model-changes --project
  src/ContentFactory.Infrastructure --startup-project src/ContentFactory.Api`
  → "No changes have been made to the model since the last migration."
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases, temporary per-fixture `DATA_ROOT`.
  Verified 2026-10-20T15:28:38Z.
- Notes: 100 fake `download` jobs were leased and finalized by two concurrent workers with
  separate DbContexts; all 100 ids and `(id, fence)` pairs were distinct. An expired lease
  could not renew or finalize; the watchdog reclaimed it and the old fence still could not
  finalize after the job was re-leased with a higher fence. An outbox replay after clearing
  `published_at` produced one downstream job because its dedupe key is derived from the
  event id. A `retry_wait` outcome with `NextRunAt` in the future was not claimable until
  the clock advanced, then reclaimed with the next fence. A PostgreSQL trigger forced the job insert to fail after the revision insert;
  the transaction rolled back and neither the revision nor the job remained. A job with a
  `submission_unknown` provider request was blocked, not returned to the queue. No provider
  API was called and no exactly-once guarantee is claimed beyond the tested DB invariants.

### T07
- Command: `dotnet test tests/ContentFactory.UnitTests --filter WorkflowTests`
  (before implementation)
  - RED: build failed with `CS0234: The type or namespace name 'Jobs' does not exist in
    the namespace 'ContentFactory.Core'` and `CS0234: ... 'Health' does not exist in the
    namespace 'ContentFactory.Worker'`. The tests described the workflow graph and lane
    limits, but those types did not exist.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter WorkerRecoveryTests`
  (before implementation)
  - RED: build failed with `CS0234` for `ContentFactory.Core.Jobs`,
    `ContentFactory.Worker.Handlers` and `ContentFactory.Worker.Health`. The failures were
    missing implementation, not a database or environment fault.
- Command: `dotnet test tests/ContentFactory.UnitTests --filter WorkflowTests`
  → 6 passed, 0 failed, 0 skipped:
  `TemplateModeSkipsCloudStages`, `OrderedStagesMatchTheApprovedPipeline`,
  `HeavyLocalLaneIsSerialized`, `CloudWaitingDoesNotHoldHeavyLocalSlot`,
  `ResourceSensorsReportUnavailableInsteadOfGuessing`, `QuietModeOptionsAreExplicit`.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter WorkerRecoveryTests`
  → 5 passed, 0 failed, 0 skipped: `PausePreventsNewHeavyWork`,
  `SleepRecoveryDoesNotFanOut`, `LostLeaseCancelsLocalProcess`,
  `GracefulShutdownStopsNewLeasesAndCancelsLocalProcess`,
  `PollRescheduleDoesNotConsumeRetryLimit`.
- Command: `make test-unit` → 37 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 40 passed, 0 failed, 0 skipped (5 worker recovery,
  7 job leasing, 5 campaign run, 8 asset ingest, 8 auth, 1 CLI, 6 schema).
- Command: `make build` → 0 warnings, 0 errors.
- Command: `dotnet ef migrations has-pending-model-changes --project
  src/ContentFactory.Infrastructure --startup-project src/ContentFactory.Api`
  → "No changes have been made to the model since the last migration."
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases, temporary per-fixture `DATA_ROOT`.
  Verified 2026-10-20T15:41:54Z.
- Notes: the D1 graph is code-defined in `WorkflowDefinition`; template-only mode has no
  cloud-submit stage and records the skipped cloud stages as `notRequiredStages` in the
  stage outbox payload, never as fake provider successes. A successful stage finalizes its
  job and inserts the child event in one PostgreSQL transaction. Replaying that event after
  a simulated worker sleep created exactly one child. Lease loss advanced a manual clock
  past the lease, the heartbeat rejected renewal, and `JobDispatcher` cancelled a real
  `/bin/sh` child; the process id was gone and the job was not finalized. Host cancellation
  stopped new leases and cancelled the same local process. A cloud poll `retry_wait` left
  the failure budget unchanged while keeping attempt numbers monotonic. Pause moved only
  queued/retry-wait `heavy_local` and `cloud_submit` jobs to `paused`; a running cloud poll
  kept its owner and lease. Heavy-local concurrency was 1 under quiet mode and a held cloud
  permit did not block the heavy-local lane. No provider API was called and no
  exactly-once provider guarantee is claimed.

### T08
- Command: `dotnet test tests/ContentFactory.UnitTests --filter ScriptValidationTests`
  (before implementation)
  - RED: build failed with `CS0234: The type or namespace name 'Campaigns' does not exist
    in the namespace 'ContentFactory.Core'`, `CS0234: ... 'Providers' does not exist in
    the namespace 'ContentFactory.Infrastructure'` and `CS0246: ScriptValidator could not
    be found`. The validator contract was absent; this was not a test assertion failure.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter PlannerAdapterTests`
  (before implementation)
  - RED: build failed with the same missing `Campaigns`/`Providers` namespaces and
    `CS0246: LmStudioScriptPlanner could not be found`. The fake-HTTP adapter surface did
    not exist yet.
- Command: `dotnet test tests/ContentFactory.UnitTests --filter ScriptValidationTests`
  → 10 passed, 0 failed, 0 skipped: `ValidScriptPasses`, `UnknownClaimIdRejected`,
  `DuplicateSceneIdsRejected`, `BenefitSceneWithoutClaimRejected`,
  `SceneCountOutsideRangeRejected`, `MultipleMotionScenesRejected`,
  `MarkupInCaptionRejected`, `UnknownAssetRejected`,
  `MockPlannerProducesValidatorApprovedScript`, `PromptAndSchemaFilesMatchD2`.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter PlannerAdapterTests`
  → 16 passed, 0 failed, 0 skipped: `ConfiguredModelIdIsSentAndUsageCaptured`,
  `ReasoningEffortNoneIsSent`, `JsonStringContentIsParsed`,
  `PromptInjectionTreatedAsData`, `MalformedJsonRepairedAtMostOnce`,
  `InvalidClaimIsRepairedOnceBeforePassing`, `UnknownJsonFieldIsRepairedOnce`,
  `SecondMalformedJsonBlocksAfterOneRepair`, `LocalModelUnavailableReturnsBlocked`,
  `LocalModelHttp503ReturnsBlocked`, `LocalModelTimeoutReturnsBlocked`,
  `TruncatedFinishReasonBlocksWithoutRepair`, `ProviderRefusalBlocksWithoutRepair`,
  `PublicBaseUrlRejectedBeforeNetworkCall`, `MissingModelIdBlocksBeforeNetworkCall`,
  `PromptVersionMismatchBlocksBeforeNetworkCall`.
- Command: `make test-unit` → 47 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 56 passed, 0 failed, 0 skipped (16 planner adapter,
  5 worker recovery, 7 job leasing, 5 campaign run, 8 asset ingest, 8 auth, 1 CLI,
  6 schema).
- Command: `make build` → 0 warnings, 0 errors.
- Command: `dotnet ef migrations has-pending-model-changes --project
  src/ContentFactory.Infrastructure --startup-project src/ContentFactory.Api`
  → "No changes have been made to the model since the last migration."
- Command: temporary console referencing `ContentFactory.Infrastructure`, calling
  `LmStudioScriptPlanner.GenerateWithTraceAsync` against `http://127.0.0.1:1234/v1`
  with `qwen/qwen3.6-35b-a3b:2`, `LM_STUDIO_REASONING_EFFORT=none` and
  `TEXT_MAX_OUTPUT_TOKENS=1536`.
  - Run 1: 8.52s, 4 scenes, 1 repair, 2,299 prompt + 823 completion tokens.
  - Run 2: 3.87s, 3 scenes, roles `hook,benefit,cta`, 0 repairs,
    911 prompt + 390 completion tokens.
  - Both outputs passed `ScriptValidator`; no paid API, model download or credential.
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases. Planner adapter tests used an in-process
  fake HTTP handler; the live smoke used LM Studio running on loopback.
  Verified 2026-10-20T16:13:00Z.
- Notes: the LM Studio base URL must be loopback and credential-free; the model ID comes
  only from configuration. Product facts are sent as a separate untrusted JSON message and
  the system prompt is a fixed embedded resource. Requests use `response_format` type
  `json_schema` built from the embedded `script-document.v1.schema.json`; LM Studio rejects
  the older `json_object` type with HTTP 400. `LM_STUDIO_REASONING_EFFORT` is optional and is
  sent only when configured; `none` disables Qwen3.6 thinking, which otherwise consumed
  1,801 reasoning tokens and truncated a 2,048-token run. The adapter parses object or
  JSON-string content, rejects unmapped fields, refuses tools/MCP/client secrets in the
  body, and sends a configured local token only in the `Authorization` header. One repair is
  allowed; malformed JSON or a failed business validation after that is `SCRIPT_INVALID`.
  Truncated, refused, timed-out and unavailable responses are blocked without a blind retry.
  The `ScriptPlanTrace` exposes prompt version, configured model ID and any response token
  usage; the plan schema has no token-usage column, so no migration was invented. The live
  LM Studio smoke is an optional local gate, not a CI dependency; CI still uses the fake
  HTTP handler and the mock provider.

### T09
- Command: `dotnet test tests/ContentFactory.UnitTests --filter LiveApprovalTests`
  (before implementation)
  - RED: build failed with `CS0234: The type or namespace name 'Billing' does not exist
    in the namespace 'ContentFactory.Infrastructure'`. The approval guard and options
    surface were absent; this was not a test assertion failure.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter BudgetRaceTests`
  (before implementation)
  - RED: build failed with the same missing `ContentFactory.Infrastructure.Billing`
    namespace and the budget-guard/price-service surfaces did not exist.
- Command: `dotnet test tests/ContentFactory.UnitTests --filter LiveApprovalTests`
  → 9 passed, 0 failed, 0 skipped: `ValidApprovalIsAccepted`,
  `PaidDisabledBlocksEvenWithValidApproval`, `MissingApprovalFileFailsClosed`,
  `MalformedApprovalFailsClosed`, `PlaceholderApprovalFailsClosed`,
  `ProviderOrModelMismatchFailsClosed`, `FallbackProviderFailsClosed`,
  `ExpiredApprovalFailsClosed`, `ApprovalExpiresDuringBatch`.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter BudgetRaceTests`
  → 15 passed, 0 failed, 0 skipped:
  `ConcurrentReservationsCannotExceedLimit`, `SettlementIsIdempotent`,
  `UnknownSubmissionKeepsReservation`, `StalePriceBlocksSpend`,
  `VerifiedPriceQuoteUsesMargin`, `PaidDisabledBlocksEveryProvider`,
  `DailyCapBlocksSpend`, `MonthlyCapBlocksSpend`, `CampaignCapBlocksSpend`,
  `VideoCapBlocksSpend`, `SingleRequestCapBlocksSpend`,
  `ApprovalRequestAndTotalCapsApply`, `ApprovalExpiresDuringBatch`,
  `ReleaseTwiceDoesNotGoNegative`, `ConfirmedRefundAdjustsSpendWithoutDoubleCounting`.
- Command: `make test-unit` → 56 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 71 passed, 0 failed, 0 skipped (15 budget race,
  16 planner adapter, 5 worker recovery, 7 job leasing, 5 campaign run, 8 asset ingest,
  8 auth, 1 CLI, 6 schema).
- Command: `make build` → 0 warnings, 0 errors.
- Command: `dotnet ef migrations has-pending-model-changes --project
  src/ContentFactory.Infrastructure --startup-project src/ContentFactory.Api`
  → "No changes have been made to the model since the last migration."
- Command: `make migrate` → applied `InitialSchema`, `AddJobClaimableIndex` and
  `AddBudgetReservations` to the local application database; done.
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases. Budget tests used a separate
  `postgres-budget` collection fixture so its migrations and rows did not overlap the
  schema/API fixture. The only approval files were temporary local test fixtures.
  Verified 2026-10-20T16:35:00Z.
- Notes: the reservation table is an internal coordination/audit record; the plan's C2
  table list did not name it, so ADR-0012 records the addition. The guard locks budget
  rows in the fixed order monthly -> daily -> campaign -> video, uses database `now()` for
  periods and timestamps, verifies the quote against the persisted price snapshot
  (including the 1.20 margin), and writes reservation + reserve cost entry in one
  transaction. Twenty concurrent reservations against a 1.00 USD cap and 0.12 USD each
  produced exactly eight successes (0.96 USD), no negative reserved/spent values, and no
  ninth reservation. Settlement/release are idempotent; an unknown submission keeps the
  reservation; a later reduced settlement writes one refund, not a duplicate. Approval
  count/total checks run after the monthly row lock, so workers cannot both consume the
  last slot; the envelope is re-read per request and also expiry-checked against database
  time. `ALLOW_PAID_APIS=false` blocked every provider name before any budget or provider
  row was written. No paid API, cloud upload, credential or publication was made.

### T10
- Command: `dotnet test tests/ContentFactory.UnitTests --filter VoiceManifestTests`
  (before implementation)
  - RED: build failed with `CS0234: The type or namespace name 'Voice' does not exist in
    the namespace 'ContentFactory.Infrastructure.Providers'`. The voice adapter,
    assembler and probe surfaces were absent; this was not a test assertion failure.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter VoiceProviderTests`
  (before implementation)
  - RED: build failed with the same missing `Providers.Voice` namespace.
- Command: `dotnet test tests/ContentFactory.UnitTests --filter VoiceManifestTests`
  → 17 passed, 0 failed, 0 skipped: `MissingNarrationNeverBecomesSilentSuccess`,
  `VoiceLocaleMismatchRejected`, `UploadedNarrationResolvesFromSceneMapping`,
  `UploadedPhraseMappingResolvesByPhraseIndex`,
  `UploadedNarrationMustMatchApprovedTextHash`, `MissingUploadedNarrationIsRejected`,
  `SsmlFromModelIsEscaped`, `CachedVoiceDoesNotChargeAgain`,
  `CacheKeyIncludesTextVoiceLocaleAndConfig`,
  `ActualAudioDurationDefinesCaptionTiming`, `SilentNarrationIsRejected`,
  `CorruptNarrationIsRejected`, `NarrationOverThirtyFiveSecondsIsBlockedWithoutRetry`,
  `PhraseSplitterNeverSplitsThaiGrapheme`,
  `RecordedFfprobeJsonProvidesMeasuredDuration`, `FileVoiceCacheSurvivesANewProviderInstance`,
  `UploadedNarrationDoesNotRequirePaidBillingScope`.
- Command: `dotnet test tests/ContentFactory.IntegrationTests --filter VoiceProviderTests`
  → 11 passed, 0 failed, 0 skipped:
  `FetchesAllowedVoicesAndSynthesizesWithEscapedSsml`, `VoiceListIsFetchedOncePerProvider`,
  `VoiceLocaleMismatchRejectedBeforeSynthesis`, `UnknownVoiceRejectedBeforeSynthesis`,
  `MissingSpeechPriceBlocksBeforeAnyHttp`, `PaidApisSwitchBlocksBeforeAnyHttp`,
  `PaidApisDisabledBlocksBeforeSynthesis`, `DefinitiveRejectionReleasesReservation`,
  `UncertainFailureKeepsReservation`, `EmptyProviderResponseKeepsReservation`,
  `UnscopedSynthesisIsRejectedWithoutHttp`.
- Command: `make build` → 0 warnings, 0 errors.
- Command: `make test-unit` → 73 passed, 0 failed, 0 skipped.
- Command: `make test-integration` → 82 passed, 0 failed, 0 skipped (15 budget race,
  11 voice provider, 16 planner adapter, 5 worker recovery, 7 job leasing, 5 campaign run,
  8 asset ingest, 8 auth, 1 CLI, 6 schema).
- Command: `dotnet ef migrations has-pending-model-changes --project
  src/ContentFactory.Infrastructure --startup-project src/ContentFactory.Api --no-build`
  → "No changes have been made to the model since the last migration."
- Command: `make test-media` → exit 2, blocked: "FFmpeg/FFprobe are not installed;
  media tests are blocked (not passed)."
- Environment: macOS arm64, .NET SDK 10.0.402 (user-local), PostgreSQL 18 in Docker at
  `127.0.0.1:54329`, `cf_test_<runid>` databases. Azure tests used an in-process fake HTTP
  handler and a recorded voice-list fixture; no live Azure or TTS call was made.
  Verified 2026-10-20T17:25:24Z.
- Notes: `UploadedVoiceProvider` resolves deterministic
  `voice:<revision>:<scene>[:p<index>]` operation ids, checks locale and an optional
  approved text hash, and never substitutes silence. `AzureSpeechVoiceProvider` requires
  both `ALLOW_PAID_APIS=true` and `EnableSynthesis=true`, fetches the allowed voice list
  from the configured endpoint, requires an exact voice/locale match, builds SSML with
  `XmlWriter` (model text escaped), obtains a verified quote, reserves through
  `IBudgetGuard` with the campaign and video/revision scope, and settles only after a valid
  audio asset is stored. A definite 400/401/403/404/422 releases the reservation; 5xx,
  timeout, malformed or empty responses keep it. `VoiceAssembler` splits only at sentence
  punctuation/whitespace, probes every phrase and combined scene, rejects corrupt, empty
  and silent narration, enforces the 35-second narration limit, and records phrase mappings
  in a canonical `VoiceManifest`. The cache key is SHA-256 over text hash, voice id, locale,
  synthesis config hash and provider version; the file cache stores only immutable asset
  references. The default `VOICE_PROVIDER=uploaded` path needs no price or reservation.
  Actual FFprobe/FFmpeg execution and the phrase concatenator remain blocked in this
  checkout because those binaries are absent; the tests use a recorded FFprobe JSON fixture
  and injected probe/concatenator doubles, not a claim that native media probing ran. The
  published adapter is not wired to a worker stage yet (T12).

### T06
Created: `src/ContentFactory.Infrastructure/Jobs/PostgresJobStore.cs`,
`Jobs/OutboxDispatcher.cs`, `Jobs/LeaseWatchdog.cs`,
`src/ContentFactory.Infrastructure/Persistence/Migrations/20261020152523_AddJobClaimableIndex.cs`
and its designer, `tests/ContentFactory.IntegrationTests/JobLeasingTests.cs`.
Modified: `src/ContentFactory.Infrastructure/Persistence/AppDbContext.cs` (claimable
partial index includes `retry_wait`), `Persistence/Migrations/AppDbContextModelSnapshot.cs`,
`tests/ContentFactory.IntegrationTests/SchemaTests.cs` (expects `ix_jobs_claimable`),
`docs/decisions.md` (ADR-0009), `docs/progress.md`.

Notes: `PostgresJobStore` claims with `FOR UPDATE SKIP LOCKED`, increments `fence` on every
claim and writes `job_attempts`. Renew/finish predicates require id, status `running`,
fence, owner and an unexpired lease. `LeaseWatchdog` returns pure/local work to
`retry_wait`, fails exhausted attempts, and blocks jobs that have any provider request so
uncertain submissions are reconciled instead of resubmitted. `OutboxDispatcher` derives
`outbox:<event-id>:<index>` dedupe keys and marks the event published in the same
transaction. The new partial index is `(lane, priority, run_after, created_at) WHERE status
IN ('queued', 'retry_wait')`.

## Files changed

### T07
Created: `src/ContentFactory.Core/Jobs/WorkflowDefinition.cs`,
`src/ContentFactory.Core/Abstractions/IJobOutboxWriter.cs`,
`src/ContentFactory.Worker/JobDispatcher.cs`,
`src/ContentFactory.Worker/Handlers/WorkflowOutboxHandler.cs`,
`src/ContentFactory.Worker/Health/ResourceGovernor.cs`,
`src/ContentFactory.Api/Endpoints/SystemEndpoints.cs`,
`tests/ContentFactory.UnitTests/WorkflowTests.cs`,
`tests/ContentFactory.IntegrationTests/WorkerRecoveryTests.cs`.
Modified: `src/ContentFactory.Worker/Program.cs` and `Worker.cs` (scoped dispatcher,
lane loop, watchdog, outbox dispatch, 30-second shutdown), `src/ContentFactory.Api/Program.cs`
(pause/resume endpoints and job store DI),
`src/ContentFactory.Infrastructure/Jobs/PostgresJobStore.cs`
(`FinishWithOutboxAsync`, monotonic attempt numbering, poll reschedule reset,
pause/resume), `tests/ContentFactory.UnitTests/ContentFactory.UnitTests.csproj` and
`tests/ContentFactory.IntegrationTests/ContentFactory.IntegrationTests.csproj` (Worker
project reference), `docs/decisions.md` (ADR-0010), `docs/progress.md`.

Notes: no schema/model change was needed. Pause/resume reuses the existing job status
column. The API endpoints are authenticated and mutations remain CSRF-protected. The
worker has no provider handler registered yet, so no paid or network AI call can occur in
this task.

### T08
Created: `src/ContentFactory.Core/Campaigns/ScriptValidator.cs`,
`src/ContentFactory.Core/Campaigns/ScriptPlanResult.cs`,
`src/ContentFactory.Core/Exceptions/ScriptPlanningException.cs`,
`src/ContentFactory.Infrastructure/Providers/LmStudio/LmStudioOptions.cs`,
`src/ContentFactory.Infrastructure/Providers/LmStudio/LmStudioScriptPlanner.cs`,
`src/ContentFactory.Infrastructure/Providers/Mock/MockScriptPlanner.cs`,
`media/prompts/script-planner.v1.txt`,
`media/schemas/script-document.v1.schema.json`,
`tests/ContentFactory.UnitTests/ScriptValidationTests.cs`,
`tests/ContentFactory.IntegrationTests/PlannerAdapterTests.cs`.
Modified: `src/ContentFactory.Infrastructure/ContentFactory.Infrastructure.csproj`
(embedded prompt and schema resources), `.env.example` (`LM_STUDIO_REASONING_EFFORT`),
`docs/decisions.md` (ADR-0011), `docs/progress.md`.

Notes: the validator is provider-independent and enforces schemaVersion 1, 3–6 scenes,
unique scene IDs, allowed roles, known claim IDs, claims on benefit/demo scenes, known
preferred assets, at most one motion scene, and plain narration/caption text. The LM Studio
adapter only calls a loopback endpoint, sends the configured model ID and embedded JSON
Schema, keeps product facts as a separate untrusted user message, rejects unmapped JSON
fields, and allows exactly one repair. `LM_STUDIO_REASONING_EFFORT=none` is recommended for
Qwen3.6 to disable hidden thinking and keep latency/heat down. The mock planner is
deterministic and offline; `TEXT_PROVIDER=mock` remains the development default. Only local
loopback requests were made; no paid API, model download or credential was used.

### T09
Created: `src/ContentFactory.Core/Exceptions/BudgetExceededException.cs`,
`Core/Exceptions/PriceStaleException.cs`, `Core/Exceptions/ApprovalStaleException.cs`,
`Core/Exceptions/PaidApisDisabledException.cs`,
`src/ContentFactory.Infrastructure/Billing/BudgetOptions.cs`,
`Billing/LiveApprovalEnvelope.cs`, `Billing/LiveApprovalGuard.cs`,
`Billing/PriceSnapshotService.cs`, `Billing/PostgresBudgetGuard.cs`,
`Billing/BillingDatabaseTime.cs`,
`src/ContentFactory.Infrastructure/Persistence/Entities/BudgetReservationRecord.cs`,
`Persistence/Migrations/20261020162647_AddBudgetReservations.cs` and its designer,
`tests/ContentFactory.UnitTests/LiveApprovalTests.cs`,
`tests/ContentFactory.IntegrationTests/BudgetRaceTests.cs`.
Modified: `src/ContentFactory.Core/Abstractions/IBudgetGuard.cs` (additive video-scope
overload with a default fallback), `src/ContentFactory.Infrastructure/Persistence/AppDbContext.cs`
(`budget_reservations` DbSet/configuration and cost-entry reservation index),
`Persistence/Migrations/AppDbContextModelSnapshot.cs`, `src/ContentFactory.Api/Program.cs`
and `src/ContentFactory.Worker/Program.cs` (budget/approval/price services in DI),
`src/ContentFactory.Api/Middleware/ExceptionMappingMiddleware.cs` (403 for paid-disabled
or approval failures, 409 for stale prices or exceeded budgets), `docs/decisions.md`
(ADR-0012), `docs/progress.md`.

Notes: no provider adapter was called. `budget_reservations` stores exact budget-period
allocations so release and settlement cannot guess or double-decrement monthly, daily,
campaign or video rows. The three-argument `IBudgetGuard.ReserveAsync` remains compatible
and uses the campaign id as the video scope; the added four-argument overload accepts a
revision/video id so T13 can enforce a real per-video cap. The local app database was
migrated; test databases are created and dropped by the integration fixture.

### T10
Created: `src/ContentFactory.Core/Abstractions/IPaidVoiceProvider.cs`,
`src/ContentFactory.Infrastructure/Billing/IVoicePriceQuoter.cs`,
`src/ContentFactory.Infrastructure/Providers/Voice/UploadedVoiceProvider.cs`,
`Providers/Voice/AzureSpeechVoiceProvider.cs`,
`src/ContentFactory.Infrastructure/Media/VoiceAssembler.cs`,
`Media/FfprobeAudioProbe.cs`, `Media/FfmpegAudioConcatenator.cs`,
`tests/ContentFactory.UnitTests/VoiceManifestTests.cs`,
`tests/ContentFactory.UnitTests/Fixtures/Voice/ffprobe-audio.json`,
`tests/ContentFactory.IntegrationTests/VoiceProviderTests.cs`,
`tests/ContentFactory.IntegrationTests/Fixtures/Voice/voices-th-TH.json`.
Modified: `src/ContentFactory.Infrastructure/Billing/PriceSnapshotService.cs`
(implements `IVoicePriceQuoter`), unit/integration test project files (copy voice fixtures),
`docs/decisions.md` (ADR-0013), `docs/progress.md`.

Notes: no database migration was added. `IPaidVoiceProvider` prevents a paid adapter from
being called through the unscoped `IVoiceProvider` method; paid synthesis requires an
explicit campaign and video/revision scope and runs through the existing PostgreSQL budget
guard. Uploaded narration is an immutable asset lookup and never incurs a TTS charge.
`FileVoiceAssetCache` persists only asset references under `DATA_ROOT/voice-cache`; the
text itself is not written to a cache path or filename. Synthetic WAV bytes in the adapter
tests are transport fixtures, not production narration acceptance.

### T04
Created: `src/ContentFactory.Infrastructure/Media/SafeProcessRunner.cs`,
`Media/ManifestHasher.cs`, `tests/ContentFactory.UnitTests/SafeProcessTests.cs`,
`tests/ContentFactory.UnitTests/ManifestHashTests.cs`.
Modified: `docs/progress.md`, `docs/decisions.md` (ADR-0007).

Notes: the runner uses `ArgumentList` and `UseShellExecute=false` (no shell), an
executable allowlist, an explicit environment allowlist, a 64 KiB per-stream output cap,
and `Kill(entireProcessTree: true)` on timeout/cancel. The manifest hash is SHA-256 over
UTF-8 canonical JSON with ordinal object-key sorting and preserved array order; enums are
camelCase strings and numbers/dates use the invariant `System.Text.Json` representation.

### T03
Created: `src/ContentFactory.Core/Assets/AssetRightsPolicy.cs`,
`src/ContentFactory.Core/Exceptions/NotFoundException.cs`,
`src/ContentFactory.Infrastructure/Storage/AssetUploadPolicy.cs`,
`Storage/FileSystemAssetStore.cs`, `Storage/AssetIngestService.cs`,
`src/ContentFactory.Api/Endpoints/AssetEndpoints.cs`,
`src/ContentFactory.Api/Middleware/ExceptionMappingMiddleware.cs`,
`tests/ContentFactory.UnitTests/AssetPathTests.cs`,
`tests/ContentFactory.IntegrationTests/AssetIngestTests.cs`.
Completed/committed uncommitted T03 helpers: `src/ContentFactory.Core/Exceptions/ValidationException.cs`,
`src/ContentFactory.Infrastructure/Storage/StorageOptions.cs`, `AssetPathResolver.cs`,
`MimeSniffer.cs`.
Modified: `src/ContentFactory.Api/Program.cs` (JSON defaults, storage DI, upload caps,
exception mapping, asset routes), `src/ContentFactory.Contracts/JsonDefaults.cs`
(shared `Configure`), `tests/ContentFactory.IntegrationTests/Api/ApiFactory.cs` and
`Api/ApiTestFixture.cs` (isolated `DATA_ROOT`), `docs/decisions.md`.

Notes: product asset upload requires `rightsStatus=owner_attested` plus a source
reference; `unknown` and `rejected` are stored but cannot be used for production or
cloud consent. Ingest streams to `DATA_ROOT/temp`, hashes while writing, fsyncs and
atomically renames into `assets/sha256/<first-2>/<hash>.<ext>`. The original filename is
never used as a path. The API test uses Thai text, spaces, a single quote and a semicolon
in the filename; the double-quote case is covered by the unit policy test because the
.NET multipart client library rejects a raw double quote in the `Content-Disposition`
parameter before the request is sent. Range previews return 206 with `Content-Range`,
and invalid ranges return 416. Cleanup enumerates only `DATA_ROOT/temp`, so a referenced
asset cannot be deleted by temp cleanup.

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
- ADR-0006: production/cloud use requires an owner rights attestation with a source
  reference; rights status is `unknown` by default.
- ADR-0007: safe local process boundary with an executable allowlist and canonical JSON
  manifest hashing.
- ADR-0008: runs freeze approved database facts into an immutable revision and dedupe
  inside the job row; client snapshots and prices are never trusted.
- ADR-0009: lease claims use `SKIP LOCKED` plus a monotonic fence; outbox replay is
  deduplicated by event id, and uncertain provider work is blocked for reconciliation.
- ADR-0010: the D1 stage graph is code-defined; lease loss cancels local work, finalize
  and child-event insertion share one transaction, and pause/resume withholds only new
  heavy/paid work without cancelling running cloud requests.
- ADR-0011: local script generation is untrusted until validated; LM Studio is loopback
  only, product facts are separate data, unknown claims and schema-invalid output are
  blocked, and at most one repair is allowed.
- ADR-0012: paid work reserves exact monthly/daily/campaign/video rows in PostgreSQL
  before submission; the internal reservation record makes settle/release idempotent,
  the approval envelope is re-read and count/total/expiry checked under the monthly lock,
  and unknown submissions keep their reservation.
- ADR-0013: narration is validated by FFprobe, cached by text/voice/locale/config/provider
  version, and paid TTS uses the same budget/live-approval guard as video; uploaded
  narration needs no charge, SSML is written with an XML writer, and missing/silent audio
  fails closed instead of becoming an empty success.
- No plan-level architecture change beyond the internal reservation table, the additive
  video-scope overload, and the internal paid-voice/price-quoter interfaces.

## Paid API activity
- Disabled; no paid requests authorized and none made.

## Next exact task
- Task ID: T11 — Timeline compiler, Thai captions and native FFmpeg renderer
- Inputs/files to read: plan T11 (`## T11` near line 1177), the C4 `ITimelineCompiler`,
  `IVideoRenderer`, `RenderManifest` and `RenderResult` contracts, D4 caption/timing
  rules, T10 `VoiceSegment`/`VoiceManifest`, T04 `SafeProcessRunner` and `ManifestHasher`,
  T03 asset storage, and the current mock video renderer path.
- First failing tests: `tests/ContentFactory.UnitTests/TimelineTests.cs` and
  `tests/ContentFactory.MediaTests/RenderGoldenTests.cs`:
  `ThaiMarksStayWithBaseCharacter`, `AssOverrideTextRenderedLiterally`,
  `CaptionOverflowBlocksRender`, `NarrationNeverCutMidWord`,
  `TimelineDurationUsesFrameGrid`, `MissingHardwareEncoderFallsBackExplicitly`.
- Prerequisite: T03, T04, T08, T10. Keep `ALLOW_PAID_APIS=false`, `AUTO_PUBLISH=false` and
  `PUBLISHING_MODE=manual_export`; no paid request or publication is allowed. A video must
  be a real MP4 produced through native media tooling, never a renamed placeholder image.
- Remaining blockers: FFmpeg/FFprobe and a Thai-capable font are missing, so native render,
  encoder/font probes and golden media tests must be reported `blocked`/`skipped`, never
  `passed`; Node 26.11.1 vs pinned 24.22.0 and port 3000 occupancy remain environment
  blockers. T10 is committed; do not start T11 until the owner reviews and says to continue.
