# Pilot and benchmark results

Everything below was measured or checked on the development machine with mock providers. No paid
request was made and no number comes from a specification sheet.

## Local benchmark (2026-10-21)

- Samples: 10 template-only clips, 0 failed (fixed synthetic fixtures, mock tone voice)
- Elapsed per clip: p50 3.4 s, p95 3.6 s, min 3.4 s, max 3.6 s; first (cold) 3.5 s
- Encoder: h264_videotoolbox (hardware: True) — Hardware encoder probe succeeded.
- FFmpeg: ffmpeg version 9.0.2 Copyright (c) 2000-2026 the FFmpeg developers
- Host: Mac17,6, 18 CPUs, 128 GiB RAM
- Swap before: total = 0.00M  used = 0.00M  free = 0.00M  (encrypted) | after: total = 0.00M  used = 0.00M  free = 0.00M  (encrypted)
- Memory pressure before: System-wide memory free percentage: 86% | after: System-wide memory free percentage: 86%
- Output on disk: 48.8 MiB for 10 clips
- Peak memory of the benchmark process: unavailable on this OS; largest single process in the tree (time -l): 203 MiB
- Temperature: not measured: no sensor is read

This is a local measurement of one machine on one day, not a model comparison or a cost figure.

## Failure-injection coverage (offline)

`dotnet test tests/ContentFactory.IntegrationTests --filter FailureInjectionTests` (9 tests) runs the
real handlers against PostgreSQL with a counting fake provider:

| Failure | Proven outcome | Test |
|---|---|---|
| Killed before submit | replay posts exactly once | `KillBeforeSubmitReplaysExactlyOnePost` |
| Killed after the provider accepted | no second POST, reservation held, poll/download blocked | `KillAfterProviderAcceptanceNeverPostsAgain` |
| Lost response | status `submission_unknown`, reservation held, no GET, no fallback | `LostResponseKeepsTheReservationAndNeverFallsBack` |
| Lease expiry | uncertain cloud job `blocked`, local job re-queued | `LeaseExpiryOnUncertainCloudWorkBlocksInsteadOfRequeueing` |
| Duplicate status/callback | one asset, one media download, reservation settled once | `DuplicateStatusAndCallbacksDownloadOnce` |
| 429 and offline/wake | poll reschedules, never a second submit | `TooManyRequestsAndOfflineNeverBecomeASecondSubmit` |
| Invalid media | rejected, nothing settled | `InvalidMediaIsRejectedAndNothingIsSettled` |
| Expired price | paid submit blocked before any reservation | `ExpiredPriceBlocksThePaidSubmit` |
| Old backup restored | queued paid work is not replayed | `RestoredSystemNeverReplaysQueuedPaidWork` |

Covered by earlier suites: disk full (`DiskPressureBlocksNewRender`), stale approval
(`ApprovalForDifferentHashRejected`, `TamperedManifestRejected`), restore with hash checks
(`BackupRestoreTests`).

## Live pilot

Not run. `scripts/live-preflight.sh` (read-only) reports:

```
FAL_KEY configured                 BLOCKED  not set; this script never creates or requests one
ALLOW_PAID_APIS                    BLOCKED  false; only the owner may change it inside a live envelope
AUTO_PUBLISH                       ok       false
PUBLISHING_MODE                    ok       manual_export
VIDEO_PROVIDER                     BLOCKED  mock; a key alone never selects live
Input schema recorded              ok       sha256 7db6f9cef954
Model id                           ok       fal-ai/wan/v2.2-a14b/image-to-video/turbo
Caps configured                    ok       daily 2.00 / monthly 30.00 / video 0.50 / request 0.20 USD
Live approval envelope             BLOCKED  no LIVE_APPROVAL_FILE; the owner must create one
Verified, unexpired price          BLOCKED  none recorded; the owner must record a verified price
Images cleared for cloud use       BLOCKED  0 of the 5 product images the pilot needs
No uncertain requests              ok       0

PREFLIGHT BLOCKED. Nothing was changed.
```

The pilot (up to 10 fal requests, 5 product images x 2 prompts, no automatic paid rerun) needs the
owner to provide a key, record a verified price, clear five product images for cloud use, create a live
approval envelope and confirm. `scripts/live-pilot.sh` deliberately stops after the preflight. Ten
samples would be an exploratory pilot, not evidence that a model is best.
