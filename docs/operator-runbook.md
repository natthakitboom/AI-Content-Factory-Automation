# Operator runbook

**Release: mock/local-only.** Everything in this version was verified with mock providers on one
Mac. No paid cloud request has ever been made, the paid pilot has not been run (see
`docs/pilot-results.md`), and nothing here posts to a platform for you. Review and posting are
always your own actions.

## Fresh clone

Requirements: macOS arm64, Docker, .NET SDK from `global.json`, Node from `.node-version`,
FFmpeg **with libass** (Homebrew `ffmpeg-full`; plain `ffmpeg` cannot burn Thai captions).

```bash
git clone <your repository> && cd <repository>
cp .env.example .env          # set DATABASE_PASSWORD; leave every key and approval path empty
chmod 600 .env
make doctor                   # read-only; exit 2 names what is missing
make verify-fast              # build, unit, integration, dashboard checks, secret scan
```

Set `CAPTION_FONT_FILE` in `.env` to a font file you are licensed to use (for example
`/System/Library/Fonts/Supplemental/Thonburi.ttc`). Its SHA-256 becomes the font identity bound to
every approval; without it the render stage stops with `CAPTION_FONT_NOT_CONFIGURED`.

Set `VOICE_PROVIDER=mock` for the offline flow. The default `uploaded` expects narration you upload,
and that path is not connected to the worker yet.

## Create the owner

```bash
make dev                                                     # PostgreSQL, API, worker, dashboard
dotnet run --project src/ContentFactory.Cli -- init-owner owner   # hidden password prompt (12+ chars)
```

Open http://127.0.0.1:3000 and sign in. Everything listens on `127.0.0.1` only.

## Run the mock flow

```bash
export CF_OWNER_PASSWORD='<the password you just set>'
./scripts/mock-flow.sh
```

It creates a sample product with an approved claim, a generated image, a channel and a campaign,
then queues a run. After about 30 seconds a video appears in **Review queue**, labelled by the
`MOCK MODE` banner. Open it, compare it with the original images and claims, tick every checklist
item and approve it, or reject it with a comment. A caption edit creates a new version that must be
reviewed again. Approving does not publish anything.

From an approved video, **Create export package** builds the ZIP (video, thumbnail, captions,
post text, product reference, posting checklist, manifest, sha256sums). Post it yourself, then record
the post URL. The product-link status stays `unknown` unless you give evidence.

## Turn live mode on and off

Live (paid) mode is the owner's decision and was never exercised in this release. It needs all of:
a `FAL_KEY` you obtained yourself, `VIDEO_PROVIDER=fal`, `ALLOW_PAID_APIS=true`, a verified price
snapshot, product images marked cloud-use-allowed, and a live approval envelope file you wrote
(`LIVE_APPROVAL_FILE`) with a model, count and total cap and an expiry. Run
`./scripts/live-preflight.sh` first; it only reads and reports. `scripts/live-pilot.sh` stops after
the preflight by design.

To turn live mode off: set `ALLOW_PAID_APIS=false` and `VIDEO_PROVIDER=mock`, restart (`make dev-stop`,
`make dev`). Pausing production (Settings) withholds new work but cannot cancel or refund cloud
requests already sent.

## Backup and restore

```bash
make backup            # verified set in ~/ContentFactoryBackups/<timestamp> (database + every asset hash)
make restore-test      # backs up, restores into a scratch database, checks hashes, removes the scratch copy
```

A backup holds no secrets and is readable only by you. Copy it to an **encrypted** location off this
computer yourself. A backup cannot stop cloud work already submitted.

To restore for real: `./scripts/restore.sh <backup-dir> <new-database> <new-data-root>`. It refuses an
existing database or a non-empty folder and asks you to type a confirmation. The restored system starts
with paid lanes **paused** and all unfinished jobs paused. Compare the cost ledger with the provider's
own history for any call after the backup time, resolve uncertain requests, then run
`dotnet run --project src/ContentFactory.Cli -- resume-paid-lanes --reconciled`.
`dotnet run --project src/ContentFactory.Cli -- cleanup` removes only unreferenced files older than
14 days and stale temp files when nothing is running.

## Cost troubleshooting

- **Costs** page: *Reserved* is money held for requests in flight or uncertain; *Spent* is recorded after
  a result arrived. They are never added into one figure.
- A **stale price** means paid work is blocked until a verified price exists.
- A job marked **Needs reconciliation** (`SUBMISSION_UNKNOWN`) may have been accepted by the provider.
  It is never retried. Check the provider's dashboard, then either
  `reconcile-attach <row-id> <request-id> <status-url> <response-url>` (the request exists) or
  `reconcile-no-charge <row-id> <reason>` (it does not), using the CLI. Both are audited.
- `PAID_APIS_DISABLED` and `PAID_LANES_PAUSED` are safe stops, not faults.

## Sleep and wake

The Mac may sleep while a cloud request runs. On wake the worker keeps polling the **same** request and
downloads before the URL expires; it never resubmits. Schedules run a missed slot only if it is less than
60 minutes late and otherwise record it as missed. Nothing registers a login item or changes power
settings; if you want the worker to survive sleep, that is a choice for you to make separately.

## Release checklist

1. `make verify` shows PASS for every gate (BLOCKED means a prerequisite is missing, not a pass).
2. `make restore-test` prints PASSED.
3. `./scripts/verify-no-secrets.sh` reports 0 findings and `.env` is not tracked.
4. `ALLOW_PAID_APIS=false`, `AUTO_PUBLISH=false`, `PUBLISHING_MODE=manual_export` in `.env`.
5. You read `docs/progress.md` (known limitations) and `docs/pilot-results.md`.
6. Only then tag it yourself: `git tag v0.1.0`. Nothing in this repository pushes or tags remotely.

## Known limitations

- Uploaded narration and Azure speech are not connected to the worker; only the mock tone is.
- Hybrid runs carry one motion scene and have never run against the real cloud provider.
- The dashboard has no screens for creating products, campaigns or schedules; use `scripts/mock-flow.sh`
  or the API.
- Temperature is not measured. Linux CI runs software encoding and says nothing about VideoToolbox.
