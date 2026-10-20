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
