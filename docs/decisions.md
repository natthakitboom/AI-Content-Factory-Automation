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

## ADR-0010 — Code-defined resumable workflows and resource lanes

- Date: 2026-10-20
- Status: accepted
- Context: the worker must restart without duplicating child jobs, must skip cloud stages
  in template-only mode, must not let a waiting cloud request consume the render slot, and
  must stop local processes when its lease is lost or the host shuts down. A pause control
  must withhold new heavy/paid work without pretending a running provider call was refunded
  or cancelled.
- Decision: `WorkflowDefinition` owns the D1 order, job types, lanes and which stages need
  cloud work. Template-only mode removes `submit_video`, `poll_video` and `download_video`
  from the sequence; the stage outbox payload records them as `notRequiredStages` and no
  fake provider success is written. `JobDispatcher` leases one lane at a time, renews the
  lease on a heartbeat, and cancels the handler and its child processes when renewal fails
  or the host stops. A successful stage finalizes the job and inserts its
  `stage_succeeded` outbox event in the same PostgreSQL transaction; `WorkflowOutboxHandler`
  derives exactly one next job and the existing event-id dedupe key makes replay harmless.
  `ResourceGovernor` uses separate semaphores per lane, forces one heavy-local slot in quiet
  mode, and exposes a database-backed count of running cloud-submit/cloud-poll jobs. Memory
  and thermal readings are reported as unavailable rather than estimated. Pause moves only
  queued or retry-wait `heavy_local` and `cloud_submit` jobs to `paused`; resume returns
  them to `queued`, while running cloud work keeps its owner and lease. A cloud poll that
  reschedules with `retry_wait` resets the failure budget; attempt numbers are derived from
  the maximum existing `job_attempts.attempt_no` so they remain monotonic.
- Consequences: worker restart after finalize recovers the child transition from the
  outbox without fan-out, and stale workers cannot commit after lease loss. Pause is not a
  refund or a provider-cancellation command, and it does not kill in-flight cloud requests.
  The cloud in-flight count is a pre-lease gate plus an in-process semaphore, not an
  exactly-once provider guarantee; provider reconciliation remains a separate concern.

## ADR-0011 — Local script generation is untrusted until validated

- Date: 2026-10-20
- Status: accepted
- Context: LM Studio is a local text source, not an authority on product facts. A product
  title, description or claim can contain text that looks like an instruction, and a model
  can return malformed JSON, unknown claim IDs, duplicate scene IDs, HTML/SSML or a
  partial response. Invalid output must never reach a paid TTS or video call.
- Decision: `LmStudioScriptPlanner` accepts only a loopback HTTP(S) base URL with no
  userinfo, query or fragment; the model ID, output limit and timeout come from
  configuration and are never guessed or downloaded. The system prompt is a fixed embedded
  resource. Product facts and the request are serialised into a separate user message
  explicitly marked as untrusted data, so text inside them cannot change the instructions.
  Requests go to `/v1/chat/completions` with a JSON Schema `response_format` built from the
  embedded `script-document.v1.schema.json`; no tools, MCP definitions or client secrets are
  sent in the body. A configured local token is sent only as an `Authorization` header.
  `LM_STUDIO_REASONING_EFFORT` is optional and is sent only when configured; `none` disables
  Qwen3.6 thinking on LM Studio's OpenAI-compatible endpoint so output tokens and heat are not
  spent on hidden reasoning. The response content may be an object or a JSON string.
  Parsing rejects unmapped fields, then `ScriptValidator` enforces schemaVersion 1, 3–6
  scenes, unique scene IDs, allowed roles, known claim IDs, claims on benefit/demo scenes,
  known preferred assets, at most one motion scene, and plain text narration/caption.
  At most one repair request is allowed with the validation errors; a second failure is
  blocked as `SCRIPT_INVALID`. Truncated, refused, timed-out or unavailable responses are
  blocked without a blind retry. `MockScriptPlanner` is the offline default.
- Consequences: an unknown claim or schema-invalid script cannot pass the planner gate, and
  the adapter exposes the configured prompt version, model ID and any token usage in a
  `ScriptPlanTrace` without inventing values. The plan schema has no token-usage column, so
  no migration was added; a later plan-script handler can persist the available trace when
  it writes the revision. A live loopback smoke test was run on 2026-10-20 against LM Studio
  with `qwen/qwen3.6-35b-a3b:2`; both runs produced validator-approved scripts, including one
  run that used the single allowed repair. Live LM Studio remains an optional local gate, not
  a CI dependency.

## ADR-0012 — Paid work reserves explicit budget rows before submission

- Date: 2026-10-20
- Status: accepted
- Context: D5 requires a price snapshot, a reservation, a settlement/release path and an
  owner approval envelope before any paid provider call. The plan's C2 table list names
  `budget_periods`, `cost_entries`, `provider_requests` and `price_snapshots` but does not
  name a per-reservation table. Without one, `ReleaseAsync(reservationId, ...)` cannot
  know which monthly/daily/campaign/video rows were debited, and a retry could decrement
  them twice. The fixed `IBudgetGuard` signature also has no video id, while a per-video
  cap must aggregate several scene requests.
- Decision: add the internal `app.budget_reservations` table. It stores the operation id
  (unique), campaign id, explicit video scope id, provider/model, price snapshot id,
  approval id, reserved/settled USD, status (`reserved`, `unknown`, `settled`, `released`)
  and a JSONB list of the exact `(budget_period_id, scope_type, amount)` allocations. The
  table is an append-only audit/coordination record, not a second source of truth for
  product or job state. `PostgresBudgetGuard` creates the monthly, daily, campaign and
  video rows lazily with `ON CONFLICT DO NOTHING`, then locks them in the fixed order
  monthly -> daily -> campaign -> video. It uses PostgreSQL `now()` for period boundaries
  and timestamps, verifies the quote against the persisted price snapshot (including the
  1.20 pilot margin), checks the per-request cap and every scope cap, and writes the
  reservation plus the `reserve` cost entry in one transaction. A duplicate operation id
  returns the existing reservation only when the provider, model and amount match; a
  conflicting retry is a 409. Settlement decrements every allocated reserved amount and
  adds the confirmed/estimated spend once; repeating the same settlement is a no-op.
  A later different settlement amount records one signed adjustment/refund entry and
  updates spend, with idempotency keyed by reservation and delta. Release is idempotent
  and refuses to release a settled reservation. An uncertain provider submission calls
  `MarkUnknownAsync`, which keeps every reserved amount and exposes the reservation for
  reconciliation; it never converts an unknown bill to zero. The three-argument
  `IBudgetGuard.ReserveAsync` uses the campaign id as a conservative video scope; the
  added four-argument overload accepts the real video/revision id and defaults to the
  three-argument method for existing implementations.
- Approval: `LiveApprovalGuard` requires `ALLOW_PAID_APIS=true` and a local, bounded
  `LIVE_APPROVAL_FILE`. It re-reads the file on every request, rejects missing, malformed,
  expired, placeholder, provider/model-mismatched and fallback-enabled envelopes, and
  requires an explicit `allowFallbackProvider:false`. Request-count and total-USD limits
  are checked from `budget_reservations` inside the same transaction after the monthly
  row is locked, so concurrent workers cannot both consume the last approval slot. The
  guard also re-checks expiry against database time. `ALLOW_PAID_APIS=false` wins over
  every provider and envelope.
- Consequences: T09 adds one migration and an internal table not named in C2; T13 must
  pass the revision/video id to the four-argument overload for a meaningful per-video cap.
  Budget period limits are immutable once a period row exists; changing an environment
  variable affects only new periods. Approval ids are cumulative, so a new live batch
  should use a new approval id. No paid request, provider adapter or publication was
  created by this task. API mappings are 403 for disabled/approval failures and 409 for
  stale prices or exhausted budgets.

## ADR-0013 — Narration is probed, cached by content identity and paid through the video guard

- Date: 2026-10-21
- Status: accepted
- Context: T10 must support owner-uploaded narration without forcing a TTS purchase, and
  optionally Azure Speech REST. A provider response, uploaded file, cache entry or model
  text cannot be trusted to define timing or to prove that usable narration exists. The
  plan requires phrase-level timing, a stable cache key, the same paid-work controls as
  video, and no silent success when narration is missing.
- Decision: `UploadedVoiceProvider` resolves an immutable audio `AssetRef` from a
  deterministic operation-id mapping (`voice:<revision>:<scene>` or
  `voice:<revision>:<scene>:p<phraseIndex>`), checks locale and an optional approved text
  hash, and never synthesizes a substitute. `AzureSpeechVoiceProvider` only runs when
  `ALLOW_PAID_APIS=true` and its explicit `EnableSynthesis` flag is set; it fetches the
  allowed voice list from the configured endpoint, requires an exact voice ID and matching
  locale, builds SSML with `XmlWriter` so model text is escaped, obtains a verified quote
  from `PriceSnapshotService`, reserves with `IBudgetGuard` using the campaign and
  video/revision scope, and settles only after storing a valid audio asset. A definite
  HTTP 400/401/403/404/422 releases the reservation; a 5xx, timeout, malformed response or
  empty audio is uncertain and keeps it. `IPaidVoiceProvider.SynthesizePaidAsync` is the
  only path that can authorize a paid call; the unscoped interface method rejects paid
  providers.
  `VoiceAssembler` splits plain narration only at sentence punctuation or whitespace
  (never inside a grapheme or word), measures every phrase and combined scene with
  `FfprobeAudioProbe`, rejects missing streams, zero duration and silent audio, and adds
  the 100 ms caption padding without exceeding the measured scene duration. Phrase
  mappings and scene segments are recorded in a canonical `VoiceManifest`. The cache key
  is SHA-256 over the text hash, voice ID, locale, synthesis config hash and provider
  version; `FileVoiceAssetCache` stores only the immutable asset reference under
  `DATA_ROOT/voice-cache`, so a restart does not pay twice and uploaded narration needs no
  reservation.
- Consequences: T10 adds no database migration and does not wire a worker stage; T11 can
  consume `VoiceSegment.DurationMs` and the phrase timings. A missing or silent file is a
  failed/blocked result, not an empty audio asset. The paid path requires an operator to
  configure a verified speech price snapshot and approval envelope; with no snapshot the
  live mode blocks before any Azure HTTP request. The demo WAV/tone used by adapter tests
  is synthetic transport data and is not production narration acceptance. Actual FFprobe/
  FFmpeg probing and concatenation remain blocked in this checkout because those binaries
  are not installed.

## ADR-0014 — Native vertical rendering uses a frame grid, fail-closed captions and probed tools

- Date: 2026-10-21
- Status: accepted
- Context: T11 must turn a validated script and measured narration into a real vertical
  product video without a cloud video API. The render must keep Thai marks attached to
  their base characters, preserve ASS override text literally, never silently drop
  captions, and refuse to produce an output when the local encoder, subtitle filter or
  font is unavailable. The task also needs deterministic timing so a later QA stage can
  compare frames and durations.
- Decision: `TimelineCompiler` compiles the immutable `product-basic.v1` template at
  1080x1920 and 30 fps. It maps each script scene to its visual by `sceneId`, requires a
  voice segment and at least one CTA, rejects blank/duplicate scene ids and missing
  narration, and enforces the 15–35 second range. Each scene duration is rounded up to the
  next frame before cumulative start times are calculated, so every segment starts and
  ends on the frame grid and the manifest's durations remain contiguous. The manifest
  records the canonical script hash, template version, renderer version and font
  fingerprint.
- Decision: there is no built-in font fingerprint. `TimelineOptions` and
  `EncoderProbeOptions` require a lowercase 64-character SHA-256 value, and
  `FontFingerprintIdentity.ComputeFromFile` hashes the licensed font file that will
  actually be burned in. A missing or malformed fingerprint fails before a render starts.
  Tests use a synthetic 64-hex value and do not claim it identifies a real font.
- Decision: `CaptionLayout` wraps by grapheme cluster, never by UTF-8 byte count, limits a
  caption to two lines and 100 ms padding, and escapes ASS control characters so model
  text is rendered literally. Literal backslashes get a word joiner after them because ASS
  has no `\\` escape; opening and closing braces use the libass `\{`/`\}` escapes. It
  writes ASS for the FFmpeg burn-in and SRT for the stored subtitle asset; ASS is not
  persisted. Width validation goes through
  `ITextWidthMeasurer`. The default `ConservativeTextMeasurer` deliberately
  over-estimates and fails closed; a production host must inject metrics from the same
  font renderer it burns in. T12 must also run the technical QA bounds check on the
  actual rendered captions. Captions are never shrunk, truncated or skipped to make a
  render pass.
- Decision: `EncoderProbe` treats a hardware encoder listed by FFmpeg as untrusted until a
  real probe encode succeeds. It probes `h264_videotoolbox`, then falls back explicitly to
  `libx264` with bounded threads, and fails closed when the `subtitles`/libass filter or a
  Thai-capable font is not available. `FfmpegVideoRenderer` validates the manifest and
  captions before starting a process, copies local inputs to UUID temp paths, builds the
  filter graph with escaped paths, burns the generated ASS, extracts a thumbnail at the
  template frame (not blindly the first frame), verifies the MP4/JPEG magic bytes, and
  registers content-addressed video, thumbnail and SRT assets only after success.
  `IProcessRunner` keeps process execution behind `SafeProcessRunner` and makes encoder,
  caption and failure tests possible without the native tools.
- Consequences: the manifest identity changes when the template, renderer or font
  changes, so an approval cannot silently authorize a different render. T11 adds no
  database migration and no worker handler; T12 wires the compiler and renderer into the
  job pipeline and performs the full media gate. In this checkout FFmpeg, FFprobe and a
  Thai font are absent, so the real MP4, encoder probes and golden-frame tests are
  `blocked`/`skipped`, never `passed`. No paid API, credential, cloud upload or publication
  is involved.
- Notes: the product-basic template is code-defined constants because the T11 file list
  did not include a configuration loader. Moving it to versioned JSON later must preserve
  the same manifest fields and invalidation behavior. `SafeProcessRunner` now implements
  `IProcessRunner`; its argument-list and environment-allowlist behavior is unchanged.

## ADR-0015 — Technical QA runs on the actual render, binds the exact manifest and keeps invalid output

- Date: 2026-10-21
- Status: accepted
- Context: T12 must validate a rendered video before it can move to owner review. The
  check must use the real media streams, not the renderer's declared metadata, because a
  renderer bug or an interrupted write could produce an invalid file. It must also bind the
  exact manifest identity, so a QA result cannot be reused for a different script, template
  or font version. Invalid output must remain available for debugging under retention rules;
  deleting it and claiming success is not allowed.
- Decision: `TechnicalQa` is the sole implementation of `ITechnicalQa`. It copies the
  content-addressed video asset to a UUID temp path and runs FFprobe and FFmpeg against the
  real bytes. It reports errors for: corrupt or unreadable files, missing audio, wrong
  dimensions/codecs/pixel format, out-of-range duration, A/V duration drift over 200 ms,
  decode failure, silent narration, and malformed or overflowing SRT cues. Black frames are
  reported as warnings because intentional title or end cards are allowed by the template;
  they never turn a failed clip into a pass and they never alone fail an otherwise valid
  clip.
- Decision: `TechnicalQaHandler` is the worker stage that calls `ITechnicalQa`. It
  deserializes a payload of `{ videoVersionId, renderResult }`, loads the video version
  from PostgreSQL, verifies that the asset rows exist, that the output asset id matches, and
  that the persisted manifest JSON hashes to the same SHA-256 as the render result. It
  writes structured `qa_json` with passed/errors/warnings, manifest hash, duration and
  checked time. On pass it sets `awaiting_review`; on a failing QA report it sets
  `qa_failed` and returns `QA_FAILED`; on tool unavailability, invalid payloads, missing
  assets or manifest mismatches it sets `blocked`. The rendered video/thumbnail/subtitle
  assets are never deleted on failure.
- Consequences: a video version only reaches `awaiting_review` when the stored bytes match
  the approved manifest. The QA report is durable, readable and contains stable error codes,
  so a later owner review and export can trust it. T12 adds no database migration and does
  not wire the full compile/render/voice pipeline; that end-to-end orchestration is T15. In
  this checkout FFmpeg/FFprobe and a Thai-capable font are absent, so the real-media QA
  fixtures are skipped; the reported counts do not claim a rendered MP4 passed QA. No paid
  API, credential, cloud upload or publication was used.

## ADR-0016 — Cloud video submission uses exact fal D6 request, holds budget on uncertain outcome and never retries the POST blindly

- Date: 2026-10-21
- Status: accepted
- Context: T13 sends image-to-video requests to fal.ai. A paid cloud request is
  billable once the provider accepts it, so the local adapter must not invent fields,
  must normalize the image before exposing it, must persist an attempt before the POST,
  and must treat a dropped connection as an uncertain outcome rather than a failure that
  can be retried automatically.
- Decision: The fal adapter sends the exact D6 schema to
  `https://queue.fal.run/fal-ai/wan/v2.2-a14b/image-to-video/turbo` with
  `Authorization: Key <FAL_KEY>`. `FalRequestBuilder` decodes the source image with
  SkiaSharp, resizes the long edge to at most 1280 px, re-encodes as JPEG to strip
  metadata, and refuses inputs that exceed the 3 MiB pre-base64 or 5 MiB request-body
  caps. The request body contains only `image_url`, `prompt`, `resolution`,
  `aspect_ratio`, `enable_safety_checker`, `enable_output_safety_checker`,
  `enable_prompt_expansion`, `acceleration`, `video_quality` and `video_write_mode`.
  No `duration`, no invented parameters and no local path are ever sent.
- Decision: `SubmitVideoHandler` is idempotent. It looks up `provider_requests` by the
  client operation id and returns the existing outcome if a row was already written. For
  a new attempt it verifies the asset row exists, checks `CloudUseAllowed`, reserves the
  budget, writes a `prepared` row, moves it to `submitting`, and only then performs the
  HTTP POST. A dropped connection or ambiguous HTTP status results in
  `SubmissionUnknownException`, a `submission_unknown` row and a `SUBMISSION_UNKNOWN`
  job failure while the budget reservation stays held for owner reconciliation. A
  definitive 4xx client error results in `ProviderRejectedException`, a
  `failed_confirmed` row, and a released reservation. There is no generic retry policy
  around the billable POST.
- Consequences: the system cannot double-submit a scene because the provider request row
  is the fence. An uncertain submission does not lose money or create a fake success; it
  surfaces a stable error code and waits for human reconciliation. The adapter depends on
  SkiaSharp for image normalization and adds `cancel_url` to `provider_requests` for T14.
  No real FAL_KEY, paid call, credential, public URL or publication was used in
  development or tests.

## ADR-0017: Recovery of cloud generations and safe media ingest (T14)

- Decision: poll and download jobs inherit the submit payload and resolve exactly one
  `provider_requests` row by client operation id. They never call submit. Rows in
  `prepared`, `submitting` or `submission_unknown`, or without queue references, are
  blocked with `UNKNOWN_REQUIRES_RECONCILIATION`; only the owner can attach an existing
  request id (URLs must be HTTPS on the queue host and end in `/requests/<id>[/status|/cancel]`)
  or confirm no charge with a reason. Both write an audit event.
- Decision: GET calls to the queue retry on 429, 5xx and network errors with Retry-After and
  jitter, then fall back to "still in progress". The fal key is only sent to the configured
  queue host. Cancel uses `PUT` (confirmed against fal docs) and does not release the
  reservation; a failed generation also keeps it held. Release happens only through owner
  no-charge confirmation, and a delivered output settles it at the price snapshot (`estimated`).
- Decision: the signed output URL is never stored. Download re-polls the existing request,
  refreshes once on 401/403/404/410, then streams into the content-addressed store.
  `provider_requests.output_asset_id` plus hash dedupe makes a replay after a crash idempotent.
- Decision: `SafeMediaDownloader` allows HTTPS port 443 on an explicit host allowlist
  (`fal.media`, `fal.run` and subdomains; extend by configuration after observing real hosts),
  rejects IP literals and userinfo, accepts IPv6 only in global unicast 2000::/3 minus Teredo, 6to4 and documentation ranges (security review follow-up), validates DNS for every redirect hop (max 3), denies
  private/loopback/link-local/CGNAT/multicast ranges, caps 200 MiB and 180 s, and sends no
  credentials. The production handler pins the connection to the validated address.
- Consequences: the allowlist is a guess until a real output is seen; an unexpected host fails
  closed. Live status/response JSON shapes were taken from the plan and docs, not exercised.

## ADR-0018: Offline pipeline wiring and mock/live separation (T15)

- Decision: provider choice comes only from explicit `TEXT_PROVIDER`, `VIDEO_PROVIDER` and
  `VOICE_PROVIDER` values (`ProviderModes`). A credential in the environment never selects
  live; unknown names fail. Mock mode is reported as `MOCK MODE` by `GET /api/v1/system/mode`.
  `SubmitVideoHandler` additionally blocks a billable provider with `PAID_APIS_DISABLED`
  before any quote, reservation or HTTP request when `ALLOW_PAID_APIS=false`.
- Decision: the mock cloud path uses the real code: `MockVideoProvider` (never billable) feeds
  the real poll and download handlers through `SafeMediaDownloader`, whose HTTP handler only
  serves a locally generated clip and counts every refused external request.
- Decision: shared contract changes are additive. `JobOutcome` gained optional
  `NextPayloadJson` (existing callers unchanged) so a stage can hand data to the next stage;
  `IVideoProvider.IsBillable` defaults to true so every live adapter stays billable. The run
  payload is never rewritten, only extended.
- Decision: render services that need the caption font are built on first use from
  `CAPTION_FONT_FILE`; without it the render stages return `CAPTION_FONT_NOT_CONFIGURED`
  instead of crashing the worker or inventing a fingerprint.
- Decision: the renderer converts stills to limited-range yuv420p explicitly (a JPEG otherwise
  yields yuvj420p and fails technical QA).
- Consequences / known gaps: uploaded narration and Azure voice are not wired to the worker
  (`VOICE_UPLOAD_NOT_WIRED` / `VOICE_PROVIDER_NOT_CONFIGURED`), a hybrid run carries exactly
  one motion scene, and prepare_review only confirms the version is awaiting review. Approval
  stays an owner action (T16).

## ADR-0019: Review and export backend (T16/T17)

- Decision: `ReviewService` lives in `Infrastructure/Review` (the plan names `Core/Review`, but Core
  has no database access). An approval is bound to one manifest hash; the version row is locked
  `FOR UPDATE`, the stored manifest hash is recomputed, QA must have passed and the seven E2
  checklist items must all be true. The reviewer comes from the session claim only; there is no
  bulk or "approve all" route.
- Decision: a caption edit creates a new content revision (scenes, images and narration are reused)
  and queues only `compile_timeline`, so a different manifest hash must be reviewed again and no
  provider is called. The earlier approval stays attached to its own hash.
- Decision: `video_versions` now records the thumbnail and subtitle assets
  (`AddVideoVersionOutputs`); `AssetKind.Archive` is sniffed as `application/zip`, capped at
  video + 32 MiB, and cannot be requested through the upload API.
- Decision: `ExportService` re-checks at export time: approved status, approval hash equals the
  recomputed manifest hash, current owner attestation for every source asset, an offer checked
  within 24 hours, and an active channel in the product market. The ZIP is reproducible (fixed
  entry times, fixed order), so replay returns the same asset. It records no publication; the
  owner records one afterwards and the product-link status stays `unknown` without evidence.

## ADR-0020: Scheduling policy (T18)

- Decision: a slot is a local date, time and IANA zone (key `2026-10-21T09:00@Asia/Bangkok`,
  formatted with the invariant culture so a Buddhist-calendar locale cannot change keys). A slot at
  most 60 minutes old runs once; older slots are audited as `schedule.missed` and never replayed;
  a non-existent DST local time is audited as `schedule.dst_skipped`; a repeated local time uses its
  first occurrence. `schedules.last_slot` only moves forward, so each slot is judged once.
- Decision: a scheduled run is created through `RunEnqueuer` with zero cloud scenes and no paid
  approval, so budget, approval and resource guards still decide all spending. Daily limits count
  `schedule_occurrences` in PostgreSQL, which survives restarts. Schedules are created disabled.

## ADR-0021: Analytics import and aggregation (T19)

- Decision: CSV import needs an explicit mapping (source, as-of or as-of column, publication column and
  kind, each metric column, currency for money). Rows are keyed by source, publication and as-of, so a
  repeat import changes nothing and a corrected row updates in place with a `metrics.row_updated` audit
  event holding the previous values. Returned commission is subtracted from settled commission.
- Decision: the overview uses the latest cumulative snapshot per publication, keeps unreported numbers
  null, returns null ratios for a missing or zero denominator (using only posts that reported both
  numbers), separates currencies, and is produced per source because two sources can report the same
  post. The CSV reader never evaluates formulas and enforces size limits.

## ADR-0022: Retention, backup and restore safety (T20)

- Decision: `RetentionService` removes an asset only when it is older than 14 days and referenced by
  no foreign key, export package, stored snapshot/script/manifest, or unfinished job; temp files
  older than 24 h are removed only when no job is running. `DiskGuard` makes `render_video` return
  `DISK_PRESSURE` below `MIN_FREE_DISK_GIB` or `MIN_FREE_DISK_RATIO`; it never deletes anything.
- Decision: backup quiesces through a durable `system_flags.write_quiesce` flag (the API refuses
  mutating requests with 503) and pauses queued jobs, drains local running jobs, dumps with the
  container's own `pg_dump` (no host client or password on a command line), copies every asset while
  hashing it, and writes an owner-only set with no environment secrets. In-flight cloud requests are
  listed in the manifest; a backup cannot stop cloud billing.
- Decision: restore needs a new database name, a new or empty data root and the typed confirmation,
  verifies every hash before creating anything, cleans up after a failure, and starts with
  `paid_lanes_paused=true` and all unfinished jobs paused. `submit_video` returns `PAID_LANES_PAUSED`
  until the operator runs `resume-paid-lanes --reconciled`.

## ADR-0023: Dashboard architecture and end-to-end harness (T16, T17, T19, T21)

- Decision: `apps/dashboard` is a Next.js App Router app of client components. The browser only
  talks to its own origin; `next.config.mjs` rewrites `/api/*` to the loopback API (and refuses a
  non-loopback `API_ORIGIN`), so CORS stays off, the `SameSite=Strict` cookie is first-party and no
  server secret is bundled. CSP, nosniff, no-referrer and frame denial are set as headers.
- Decision: the API client sends the double-submit CSRF token on every mutation and refreshes it once
  after a rejection. TypeScript DTOs are hand-written because the API publishes no OpenAPI document;
  generating them is a follow-up.
- Decision: status is always text plus a glyph, never colour alone; money is shown as reserved,
  spent and held-for-unknown, never as one number; a missing metric shows "No data"; no job offers a
  retry; the mode banner comes from `GET /api/v1/system/mode`.
- Decision: the end-to-end run (`npm run test:e2e`) creates a scratch `cf_test_e2e_*` database,
  fills it by running the real mock pipeline (so the MP4s are real), starts the API, the worker and
  the dashboard on loopback, and drops everything afterwards. It never touches the app database.
- Known gap: the UI for each task was written before its Playwright spec, so the e2e RED step was
  not observed for T16/T17/T19/T21 (the API-level tests of those tasks did have a RED step).

