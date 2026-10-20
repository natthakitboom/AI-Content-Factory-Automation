# Architecture decisions

Records decisions that change or clarify the plan. The plan remains the source of truth;
this file explains local choices and their consequences.

## ADR-0001 — .NET 10 SDK installed user-local

- Date: 2026-10-20
- Status: accepted
- Context: the machine had only .NET 9 SDK. The plan requires .NET 10 LTS and forbids
  starting new work on .NET 8/9.
- Decision: install SDK `10.0.402` into `~/.dotnet` without sudo; pin it in `global.json`;
  `make` prepends `~/.dotnet` to `PATH`; `scripts/doctor.sh` fails when a 10.x SDK is
  absent.
- Consequences: direct `dotnet` invocations must use the Makefile or add `~/.dotnet` to
  `PATH`. No system-wide change was made.

## ADR-0002 — Central package management

- Date: 2026-10-20
- Status: accepted
- Decision: all NuGet versions live in `Directory.Packages.props`; project files carry no
  `Version` attributes. Versions are exact, never floating.
- Consequences: dependency upgrades are one-file changes and reviewable.

## ADR-0003 — Integration tests use real PostgreSQL only

- Date: 2026-10-20
- Status: accepted
- Context: leases, unique constraints, numeric precision and transactional outbox cannot
  be proven with the EF in-memory provider.
- Decision: integration tests connect through an explicit `CF_TEST_POSTGRES` maintenance
  connection, create `cf_test_<runid>` databases, apply real migrations and drop only
  those databases. There is no fallback to the app database.
- Consequences: `make test-integration` exits 2 when the variable or server is missing;
  that is reported as blocked, never passed.

## ADR-0004 — Assets are content-addressed; rights are separate

- Date: 2026-10-20
- Status: accepted
- Decision: file identity is SHA-256 plus kind; the on-disk path is derived only from
  the hash and canonical MIME extension. User filenames are never used as paths. Rights
  status, source reference and cloud-use consent are separate database fields.
- Consequences: re-uploading identical bytes reuses one immutable file; cloud upload is
  opt-in per asset and defaults to false.

## ADR-0005 — Money and time representations

- Date: 2026-10-20
- Status: accepted
- Decision: API spend uses `decimal` and PostgreSQL `numeric(18,6)`. Timestamps use
  `DateTimeOffset` mapped to `timestamptz`; DB time is used for leases and budget locks.
  Product prices and commissions retain their source currency.
- Consequences: no floating-point money anywhere; currency mixing without an exchange
  rate and date is rejected.

## ADR-0006 — Rights attestation gates production and cloud use

- Date: 2026-10-20
- Status: accepted
- Context: a content hash proves identity, not permission. A product image copied from a
  marketplace or found online cannot be treated as licensed for advertising, and cloud
  upload needs an explicit consent decision.
- Decision: `rights_status` is `unknown` by default. `owner_attested` requires a non-empty
  `source_reference` (at most 1024 characters); `rejected` blocks production. Attaching an
  upload to a product (`POST /products/{id}/assets`) and setting `cloud_use_allowed=true`
  both require an owner attestation. Re-uploading identical bytes never resets an existing
  attestation to `unknown`.
- Consequences: generated or imported assets can still be staged with unknown rights, but
  they cannot enter a production run or a cloud call until the owner attests them.

## ADR-0007 — Safe local process boundary

- Date: 2026-10-20
- Status: accepted
- Context: media commands must never be assembled through a shell, and a child process must
  not receive provider keys or database secrets by accident.
- Decision: `SafeProcessRunner` starts only allowlisted executables with
  `UseShellExecute=false` and `ArgumentList`. The child environment is cleared and rebuilt
  from an explicit allowlist (`PATH`, `HOME`, temp/locale and fontconfig variables). Both
  output streams are capped; timeout or cancellation kills the entire process tree.
  `ManifestHasher` hashes canonical JSON with ordinal key sorting and preserved array order.
- Consequences: adding a new tool requires an explicit allowlist entry. Callers must not
  log raw process output that may contain user paths or media content; the runner itself
  never includes arguments or output in exception messages.

## ADR-0008 — Runs freeze approved facts and dedupe inside the job row

- Date: 2026-10-20
- Status: accepted
- Context: a campaign run must be reproducible and retry-safe without adding a second
  idempotency store before T06 introduces the durable job store. Client-supplied snapshots
  and prices cannot be trusted, and a later product edit must not rewrite history.
- Decision: `POST /campaigns/{id}/runs` rebuilds the `ProductSnapshot` from PostgreSQL
  (approved, unexpired claims and owner-attested assets only), serialises it into an
  immutable `content_revisions` row, and inserts the first `plan_script` job in the same
  transaction. The job's `dedupe_key` binds the campaign and the `Idempotency-Key`; its
  JSON payload stores the canonical request hash and the revision id. Same key plus same
  body returns the original `{jobId, revisionId}`; same key plus a different body returns
  `409 IDEMPOTENCY_CONFLICT`. Revision numbering locks the campaign row (`FOR UPDATE`).
- Consequences: no provider call happens while enqueuing. Cloud video scene limits above
  zero are rejected until a live paid-work approval exists, and the API never accepts a
  product snapshot, claim or price from the request body.

## ADR-0009 — Lease with a fence; replay the outbox by event id

- Date: 2026-10-20
- Status: accepted
- Context: a worker may sleep, lose its database session or be replaced while a local job is
  still running. Reclaiming the same job without a token lets the old worker commit a stale
  result. Provider calls add a second hazard: an uncertain submission must never be sent
  again automatically.
- Decision: `PostgresJobStore` claims eligible `queued`/`retry_wait` rows in a short
  transaction with `FOR UPDATE SKIP LOCKED`. Every claim increments `fence`, sets
  `lease_owner` and a 120-second lease, and writes a `job_attempts` row. Renew and finish
  predicates require id, status, fence, owner and `lease_until > now`; a mismatch returns
  false and commits nothing. The watchdog moves expired pure/local jobs to `retry_wait`,
  marks exhausted attempts `failed`, and blocks any job that has a provider request for
  reconciliation instead of resubmitting it. `OutboxDispatcher` locks one unpublished event,
  asks a local handler for downstream jobs, inserts them with
  `outbox:<event-id>:<index>` dedupe keys and marks the event published in the same
  transaction.
- Consequences: a stale worker cannot finalize after another claim. Replaying an event
  cannot create a second downstream job. The system claims only database invariants, not
  exactly-once provider execution; uncertain provider work requires explicit reconciliation.
