# AGENTS.md — AI Content Factory

This file is the operating contract for coding agents working in this repository.
Read it before every task.

## Required reading before implementation

1. `AGENTS.md` (this file)
2. `docs/START_HERE_DEEPSEEK.md`
3. The relevant task in `docs/AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md` (sections A–F for
   contracts and constraints; section G for the task; sections H–J before live work)
4. `docs/progress.md` for the last verified state

The plan file is the source of truth. Do not invent endpoints, schema columns, provider
parameters, prices or model IDs that are not in it.

## Non-negotiable safety defaults

- `ALLOW_PAID_APIS=false` and `AUTO_PUBLISH=false` at all times unless the owner has
  explicitly created a live approval envelope.
- `PUBLISHING_MODE=manual_export`; never publish or claim a publication happened.
- `TEXT_PROVIDER=mock` and `VIDEO_PROVIDER=mock` for development and tests.
- Never call a paid API, request credits, create accounts, or use real credentials.
- Never bind APIs or dashboards to `0.0.0.0`; loopback only. No public tunnels,
  router port forwarding, wildcard CORS or public object storage.
- Do not add Redis, MinIO, n8n, Kubernetes, vector databases or other large services
  to the MVP critical path.
- Never commit secrets, real product images, personal data or HR files. `.env` is local
  and ignored. `.env.example` contains names and safe defaults only.
- Do not scrape credentials from OpenCode or other tools.

## Development workflow (RED → GREEN → REVIEW)

For every task:

1. Inspect existing files first. Do not overwrite unrelated user work.
2. Write the failing tests named in the task before implementation.
3. Run the exact test command and record the real RED output.
4. Implement the smallest coherent change that satisfies the task and the plan contracts.
5. Run the tests again. Only report PASS when the command actually ran and passed.
6. Update `docs/progress.md` with files changed, command, exit code, pass/fail/skip
   counts, environment and timestamp, and blockers.
7. Commit only related files when git is configured. Never invent a git identity and
   never commit `.env`, generated media or credentials.

If a prerequisite is missing (FFmpeg, LM Studio, Docker, database), report it as
`blocked` — never as `passed`, and never install software with sudo without permission.

## Architecture rules that tests must enforce

- PostgreSQL is the source of truth for jobs, state and budgets. The UI is not.
- Assets are content-addressed and immutable. Database rows store hashes and relative
  paths, not binary blobs.
- Paid work requires a price snapshot, atomic reservation, and an attempt record before
  submission. An uncertain submission is never retried blindly.
- Worker writes must verify lease ownership and fence in the same transaction.
- Every product claim used by a script must be approved, unexpired and traceable.
- Thai text, filenames and arguments are never turned into shell commands or disk paths.
- Manifests are hashed canonically; approvals bind exactly one manifest hash.
- Integration tests use real PostgreSQL. The database name must start with `cf_test_`
  and the connection must come from an explicit test variable, never the app connection.
- No in-memory database is accepted as proof of locking, constraints or transactions.

## Scope discipline

- Implement only the task in front of you. Do not pre-build future milestones.
- Do not change shared contracts without explaining downstream impact and updating all
  callers and tests in the same change.
- Record architecture changes in `docs/decisions.md`.
- Stop at the milestone gate (M0 = T00–T05) and hand back a summary for owner review.

## Environment facts for this checkout

- macOS arm64; .NET 10 SDK is installed user-local at `~/.dotnet` (no sudo); Node is
  pinned to 24.22.0 in `.node-version`.
- `make` prepends `~/.dotnet` to `PATH`.
- FFmpeg/FFprobe and LM Studio may be absent. Pure unit and PostgreSQL tests must still
  run; media/live gates are reported as blocked.
