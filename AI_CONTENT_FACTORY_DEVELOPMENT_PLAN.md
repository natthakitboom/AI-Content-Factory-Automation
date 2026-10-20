# AI Content Factory — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> หาก OpenCode ไม่มี skills ข้างต้น ให้ทำตามลำดับงานและวงจร RED → GREEN → REVIEW ในเอกสารนี้โดยตรง ไม่ต้องติดตั้ง plugin เพิ่ม และห้ามอ้างว่าเรียกใช้ skill ที่ไม่มีอยู่จริง

**Goal:** สร้างระบบอัตโนมัติบน Mac ของผู้ใช้ เพื่อเปลี่ยนข้อมูลสินค้าและสื่อที่มีสิทธิ์ใช้งานเป็นวิดีโอแนวตั้ง พร้อมเสียง คำบรรยาย การตรวจคุณภาพ การอนุมัติ และชุดไฟล์พร้อมเผยแพร่ โดยควบคุมค่า API และกู้คืนงานได้หลังเครื่องหยุดทำงาน

**Architecture:** Local-first modular monolith + native macOS worker + PostgreSQL ใน Docker. ใช้ LM Studio สำหรับงานข้อความระหว่างผลิต ใช้ fal.ai เฉพาะฉาก AI motion และใช้ FFmpeg บน macOS ประกอบ MP4. แยกเครื่องมือช่วยเขียนโค้ด OpenCode Go ออกจากบริการ AI ที่ระบบเรียกใช้ประจำวัน และไม่สมมติว่า TikTok รองรับการโพสต์พร้อมปักสินค้าอัตโนมัติ

**Tech Stack:** ASP.NET Core/.NET 10 LTS, EF Core + Npgsql, PostgreSQL 18, Next.js 16 + TypeScript, Node.js 24 LTS, FFmpeg/FFprobe, LM Studio, fal HTTP Queue API, Azure Speech REST แบบเลือกเปิด, xUnit, Vitest, Playwright, Docker Compose.

**Spec:** ข้อกำหนดหลักอยู่ในไฟล์เดียวกัน ส่วน A–F; แผนลงมือพัฒนาอยู่ส่วน G; คู่มือปฏิบัติการและการส่งต่ออยู่ส่วน H–J. ไม่ต้องอาศัยข้อความจากแชตก่อนหน้าเพื่อพัฒนา

**วันที่จัดทำ/ตรวจเอกสารภายนอก:** 20 ตุลาคม 2026 (Asia/Bangkok)

**สถานะ:** เอกสารออกแบบและแผนพัฒนา ไม่ใช่ซอร์สโค้ดสำเร็จรูป ไม่ได้รัน benchmark หรือทดสอบ API แบบเสียเงินจริงบนเครื่องของผู้ใช้

## Global Constraints

- เป้าหมายฮาร์ดแวร์ตามที่ผู้ใช้แจ้ง: MacBook Pro M5 Pro, unified memory 128GB; ตรวจความจริงด้วยคำสั่งใน T00 โดยไม่เก็บ serial number ใน repository
- ใช้ .NET 10 และ Node 24 รุ่น patch ที่ยังได้รับการสนับสนุน ณ วันเริ่มงาน; pin patch จริงใน lock/config หลังตรวจแล้ว ห้ามใช้ preview หรือ `latest` ลอยใน production
- เริ่มเป็นผู้ใช้คนเดียว เครื่องเดียว และ 1 ช่องภาษาไทย; โครงสร้างรองรับ `market`, `locale`, `channel_id` แต่ไม่สร้าง multi-tenant SaaS
- API, dashboard, PostgreSQL และ LM Studio ต้องรับการเชื่อมต่อเฉพาะ loopback ใน MVP; ไม่มี public tunnel หรือ router port forwarding
- PostgreSQL คือแหล่งความจริงของ job/state/budget; ห้ามให้ n8n หรือ UI เป็นผู้ถือสถานะงานหลัก
- FFmpeg และ LM Studio รัน native บน macOS; PostgreSQL อยู่ Docker; n8n/Redis/MinIO/Kubernetes ไม่ใช่ dependency ของ MVP
- `ALLOW_PAID_APIS=false`, `PUBLISHING_MODE=manual_export`, `AUTO_PUBLISH=false` เป็นค่าเริ่มต้นเสมอ
- ห้ามใช้ OpenCode Go subscription เป็น runtime สำหรับผลิตสคริปต์โฆษณาเป็น batch โดยสมมติว่าได้รับอนุญาต; ใช้ Go ช่วยพัฒนาโค้ดเท่านั้น เว้นแต่ผู้ให้บริการยืนยันกรณีใช้งานอื่นชัดเจน
- ห้ามสร้างบัญชีปลอม ปั่น engagement วอร์มช่องด้วยบอต หลบ rate limit หรือทำ browser automation เพื่อเลี่ยงข้อจำกัดของแพลตฟอร์ม
- AI ผลิตได้เฉพาะข้อมูลสินค้าที่อ้างอิง `claim_id` ที่อนุมัติแล้ว; ไม่มีคำอ้างรักษาโรค รับประกันผล หรือประสบการณ์ผู้ใช้ปลอม
- ค่าเงินต้นทุน API ใช้ USD แบบ `decimal` และ PostgreSQL `numeric(18,6)`; ข้อมูลราคา/ยอดขายคงสกุลเดิม ห้ามรวม THB/EUR/USD โดยไม่มีอัตราแลกเปลี่ยนพร้อมวันที่
- การเรียก API ที่เสียเงินต้องมีราคา snapshot, การจองงบแบบ atomic และบันทึก attempt ก่อนส่ง; ไม่ retry POST ที่ไม่ทราบว่ารับงานแล้วหรือไม่
- ต้องทำ end-to-end ใน mock mode ได้โดยไม่มี API key และไม่มีการออกอินเทอร์เน็ตไปยัง AI provider
- ทุก milestone ต้องมีคำสั่งทดสอบและหลักฐานผลจริง; ห้ามเขียนว่า PASS หากไม่ได้รัน
- ห้ามเอา API key, token, ภาพส่วนตัว, ข้อมูลองค์กร หรือไฟล์ HR ของผู้ใช้ใส่ fixtures, prompt logs, commits หรือ cloud requests

## Review Focus

1. **ส่งงานสำเร็จที่ provider แต่ response หาย:** ต้องเข้าสู่ `SUBMISSION_UNKNOWN` คงเงินจอง และไม่สร้างคำขอเสียเงินซ้ำ; ทดสอบใน T09/T13/T22
2. **worker เก่ากลับมาหลัง lease หมด:** ต้องเขียนสถานะ/ผลลัพธ์ไม่ได้ด้วย fencing token; ทดสอบใน T06/T22
3. **ภาษาไทยและชื่อไฟล์มีอักขระพิเศษ:** ต้องไม่กลายเป็น shell command, ไม่ทำสระ/วรรณยุกต์แตก และไม่ซ่อน caption overflow; ทดสอบใน T03/T04/T11
4. **เปลี่ยนสื่อหลังอนุมัติ:** approval เดิมต้องไม่ใช้กับ manifest ใหม่ และ export ต้องถูกบล็อก; ทดสอบใน T16/T17
5. **Mac sleep, disk เต็ม, quota หมด หรือราคาเปลี่ยน:** ต้องพัก/กู้คืนอย่างมีสถานะ ไม่ catch up จนยิง API ทั้งงบ; ทดสอบใน T07/T18/T20/T22

---

# สารบัญ

- ส่วน A — เป้าหมาย ขอบเขต และข้อเท็จจริงที่แก้จากแบบเดิม
- ส่วน B — Architecture บน Mac และการแบ่งหน้าที่
- ส่วน C — Data model, contracts และ REST API
- ส่วน D — Workflow, budget, recovery และข้อกำหนดสื่อ
- ส่วน E — Security, policy, publishing และ analytics
- ส่วน F — Configuration และโครงสร้าง repository
- ส่วน G — แผนพัฒนา T00–T23 พร้อม test gates
- ส่วน H — Acceptance, benchmark, deployment และ backup
- ส่วน I — วิธีสั่ง DeepSeek/OpenCode และการทำงานข้าม session
- ส่วน J — เอกสารอ้างอิงและสิ่งที่ต้องตรวจใหม่ก่อนเปิดเงินจริง

---

# ส่วน A — ขอบเขตและการตัดสินใจ

## A1. สิ่งที่ต้องได้เมื่อจบ MVP

เจ้าของระบบเพิ่มสินค้า 1 รายการพร้อมข้อมูลและรูปที่อนุญาตให้ใช้ เลือก template ภาษา/ช่อง/งบ แล้วระบบสร้างวิดีโอ 15–35 วินาทีโดยอัตโนมัติจนถึงหน้าตรวจอนุมัติ จากนั้นส่งออก MP4, thumbnail, captions, disclosure, product reference และ manifest ได้ครบ

รองรับ 2 รูปแบบที่แสดงชัดใน UI:

| รูปแบบ | การผลิต | ค่า Cloud Video |
|---|---|---|
| `template_only` | ภาพ/คลิปสินค้าจริง + pan/zoom + voice + caption | ไม่เรียก Video API |
| `hybrid_motion` | รูปแบบแรก + ฉาก motion จาก Cloud API หนึ่งฉาก | คิดตามคำขอที่ใช้จริง |

การไม่เรียก Video API ไม่ได้แปลว่าไม่มีต้นทุนทั้งหมด: เสียง cloud, runtime LLM ที่เลือกใช้, ไฟฟ้า, storage และแรงตรวจยังต้องคิดแยก

**MVP มี automation จริง:** สั่งสร้างแล้วทำงานเป็น pipeline, ตั้ง schedule ได้, ทำงานต่อหลัง restart ได้ และไม่ต้องเปิดหน้าเว็บค้าง แต่ต้องเปิด Mac และ services ให้ทำงานอยู่ การปิดฝาหรือ sleep อาจหยุดงาน local

**MVP ไม่ใช่ full autopublishing:** ต้องตรวจและกดยืนยัน final video; เริ่มจาก manual export ไม่ออกแบบการปักตะกร้าอัตโนมัติจาก API ที่ยังไม่ได้รับสิทธิ์

## A2. แยก AI สามหน้าที่ให้ชัด

| หน้าที่ | ตัวเลือกที่กำหนด | ขอบเขต |
|---|---|---|
| Coding agent | DeepSeek V4.1 Flash ผ่าน OpenCode Go | อ่านแผน เขียนโค้ด ทดสอบ แก้บั๊ก |
| Runtime text AI | LM Studio + โมเดลที่ติดตั้งจริง; optional metered API แยก | script JSON, scene plan, localization |
| Runtime motion AI | fal.ai Wan 2.2 Turbo เป็น candidate เริ่มต้น | image-to-video ที่คืนคลิปจริง |

DeepSeek V4.1 Flash รับข้อความ/ภาพและสร้างข้อความ ไม่ใช่โมเดลสร้างเฟรมวิดีโอ [S02]. จึงใช้เขียนโปรแกรม FFmpeg หรือคำสั่งเรียก Video API ได้ แต่การที่ agent สร้างไฟล์ MP4 ผ่านเครื่องมือสำเร็จ ไม่ได้หมายความว่าโมเดลภาษา generate motion เอง

OpenCode Go มี DeepSeek V4.1 Flash และ API endpoint แต่เอกสารระบุวัตถุประสงค์เป็น coding-agent traffic; model availability และ limits เปลี่ยนได้ [S01]. ไม่ทำ adapter หลอก User-Agent/session เพื่อแปลงงานโรงงานคอนเทนต์ให้ดูเป็นงานเขียนโค้ด

## A3. สิ่งที่ปรับจากแนวคิดก่อนหน้า

1. ไม่เริ่ม .NET 8 สำหรับโปรเจกต์ใหม่: เลือก .NET 10 LTS เพราะ .NET 8 สิ้นสุดการสนับสนุนวันที่ 10 พฤศจิกายน 2026 ตาม Microsoft [S03]
2. ย้าย render worker จาก Linux container มา native macOS เพื่อมีทางใช้ VideoToolbox; การ composite/subtitle บางส่วนยังใช้ CPU จึงห้ามรับประกันว่าเครื่องไม่ร้อน [S08][S09]
3. ตัด Redis, MinIO และ n8n ออกจาก critical path เริ่มต้น ใช้ PostgreSQL jobs และ local filesystem ก่อน
4. ไม่บังคับ public object storage: ภาพ input ขนาดเล็กส่ง data URI ได้ตาม fal docs; Cloud ไม่สามารถอ่าน `localhost` หรือ path บน Mac เอง [S11]
5. เลือก provider จากต้นทุนต่อคลิปที่ผ่าน review ไม่ใช่ราคาหน้าป้ายอย่างเดียว
6. แยก `ready_for_review`, `approved`, `exported`, `published` และ `product_link_verified` คนละเหตุการณ์
7. ไม่มีตัวเลขรับประกัน throughput ต่อวัน; ทดสอบจริงและปรับ concurrency ทีหลัง
8. ไม่ถือว่า random seed ทำให้ AI output เหมือนเดิมทุกครั้ง และไม่ถือว่า hardware encoding ให้ MP4 bytes เหมือนกันทุกครั้ง; reproducibility หมายถึงเก็บ inputs/config/versions ย้อนกลับได้

## A4. เกณฑ์เลือก Wan เป็นตัวทดลอง ไม่ใช่ประกาศว่าดีที่สุดทุกกรณี

ราคาที่หน้า fal แสดง ณ วันที่ตรวจ [S10]:

| Config | ราคาต่อคำขอวิดีโอหนึ่งครั้ง |
|---|---:|
| Wan 2.2 Turbo, 480p | USD 0.05 |
| Wan 2.2 Turbo, 580p | USD 0.075 |
| Wan 2.2 Turbo, 720p | USD 0.10 |

นี่คือราคา **ต่อวิดีโอ** ไม่ใช่ต่อวินาที และ input schema ที่ตรวจไม่มี field `duration`, `num_frames` หรือ `fps` ให้ระบบตั้งเอง [S12]. ห้าม agent เดา parameter เพิ่มจาก endpoint Wan รุ่นอื่น

เริ่ม 720p, 9:16, input/output safety checker เปิด, prompt expansion ปิด เพื่อจำกัดการเติมคำอ้างเอง. ความยาวจริงอ่านด้วย FFprobe แล้ว timeline เลือกช่วง 3–5 วินาทีตามไฟล์ที่ได้ ไม่อ้างว่าสั่ง 5 วินาทีได้แน่นอน

ราคาเป็น snapshot อายุไม่เกิน 7 วันก่อน live run; หากราคา/หน่วยคิดเงินเปลี่ยน ให้หยุดและให้ผู้ใช้ยืนยันใหม่. ไม่เปิด model fallback ที่แพงกว่าโดยอัตโนมัติ; fallback เริ่มต้นคือใช้ภาพจริงแทนและระบุการลดรูปแบบใน review

## A5. นอกขอบเขต MVP

ไม่ทำ scraping marketplace, บัญชีจำนวนมาก, livestream rerun, voice cloning, avatar testimonial, ระบบ bidding โฆษณา, training/fine-tuning, Kubernetes, vector database, autonomous multi-agent planner หรือเชื่อม TikTok Shop ที่ยังไม่มี contract ที่ยืนยันได้

n8n เป็น optional integration หลัง MVP: สามารถเรียก REST API เพื่อ enqueue หรือส่ง notification แต่ต้องไม่เรียก paid provider ข้าม budget guard

---

# ส่วน B — Architecture สำหรับเครื่องผู้ใช้

## B1. Deployment topology

```text
DEVELOPMENT TOOL (separate from production runtime)
  OpenCode + paid Go + DeepSeek V4.1 Flash
      -> edits this repository, runs tests, produces commits
      -> never becomes the production job scheduler

MACOS HOST (native ARM64)
  Browser -> Next.js UI 127.0.0.1:3000
                 -> same-origin /api proxy
                 -> ASP.NET API 127.0.0.1:5080
                       -> PostgreSQL 127.0.0.1:54329

  .NET Worker (one process, independent resource lanes)
      -> PostgreSQL: durable jobs / leases / budgets / audit
      -> LM Studio 127.0.0.1:1234: text planning
      -> FFmpeg / FFprobe: media preparation / composition / QA
      -> local DATA_ROOT: immutable assets / temp / exports
      -> HTTPS outbound only:
            fal queue API: submit / status / result
            approved fal result hosts: download
            optional Azure Speech: narration

DOCKER
  PostgreSQL 18 + named volume + healthcheck
  optional n8n, isolated and not part of the first milestone

PUBLISHING
  approved export package -> owner uses authorized platform workflow
  future approved provider adapter only after separate capability review
```

## B2. หน้าที่แต่ละส่วน

**API** ตรวจ input/auth, สร้าง snapshot, จอง operation key, enqueue transaction, ส่งสถานะกลับทันที. ไม่รอ AI หรือ render ภายใน HTTP request

**Worker** ถือ business orchestration, ขับ state machine, ตรวจงบ, เรียก provider, renew lease และสร้างผลลัพธ์ที่ immutable. แยก task ที่ต้องรอ cloud เป็น polling jobs ไม่ยึด worker thread/DB transaction ค้าง

**UI** จัดการสินค้า, brief, scripts, preview, approve, export, schedule, jobs, cost และ settings. ห้ามเก็บ API key ใน browser หรือตั้ง `NEXT_PUBLIC_*` ให้เป็นความลับ

**PostgreSQL** มี transactional data, durable jobs, events และ outbox. EF ใช้กับ CRUD; ใช้ SQL parameterized เฉพาะส่วน lease/lock ที่ต้องชัดเจน

**Filesystem** เก็บ binary แบบ content-addressed, temporary workspace ตาม attempt และ export package แบบ immutable. DB เก็บ hash/path relative/metadata ไม่เก็บ MP4 เป็น bytea

## B3. Resource profile เริ่มต้น — ค่าทดลอง ไม่ใช่ benchmark

| ค่า | เริ่มต้น |
|---|---:|
| LM Studio concurrent requests | 1 |
| LLM context target | 8,192 tokens |
| Runtime max output | 2,048 tokens/request |
| Native FFmpeg render concurrency | 1 |
| Cloud video in-flight requests | 2 |
| TTS synthesis concurrency | 1 |
| Docker memory limit | 4–8GB สำหรับ PostgreSQL-only; เริ่ม 6GB |
| Heavy local lane | 1: ไม่ให้ LLM generation ชน render ใน quiet mode |
| New job poll | 2 seconds เมื่อ active; backoff เมื่อ idle |
| Video provider poll | 10 → 20 → 40 → 60 seconds + jitter |
| Disk reserve | max(20 GiB, 10% ของ volume) |
| Default production schedule | ปิด; เจ้าของเปิดเองหลัง pilot |

อย่าดาวน์โหลดโมเดลขนาดใหญ่โดยอัตโนมัติ. ใช้ model ID ที่ปรากฏจริงใน LM Studio; เลือกตัวเล็กที่ผ่าน script/schema benchmark ก่อน ไม่ต้องใช้ RAM ทั้ง 128GB. Cloud coding agent ไม่ได้ใช้ unified memory ของ Mac สำหรับ model inference แต่ tooling/tests บน Mac ยังใช้ทรัพยากร

---

# ส่วน C — Data model และ contracts ที่ต้องใช้ร่วมกัน

## C1. Database conventions

ใช้ schema `app`, UUID keys, `timestamptz` สำหรับเวลา, snake_case ใน DB และ camelCase ใน JSON. ค่าทางธุรกิจใช้ application enum ที่ serialize เป็น string; สร้าง CHECK constraints สำหรับสถานะที่กำหนด. ทุก entity ที่แก้ไขได้มี `version bigint` optimistic concurrency; ไม่เปิด generic CRUD ให้เขียน status ข้าม state machine

ทุกราคาสินค้าต้องมี currency; ทุก commission ต้องมี provenance. `created_at`/`updated_at` อ้าง clock ที่ inject ได้. การคำนวณ lease และ budget transaction ใช้ DB time เพื่อไม่พึ่งนาฬิกาของ worker แต่ละตัว

## C2. ตารางหลักและ invariants

| ตาราง | Fields สำคัญ | Constraint / หน้าที่ |
|---|---|---|
| `owner_users` | id, username, password_hash, active, created_at | unique username; เริ่มบัญชีเดียวจาก CLI |
| `products` | id, external_id, title, market, locale, category, version | soft archive; ไม่มีการ overwrite historical snapshot |
| `product_claims` | id, product_id, text, source_reference, verified_at, approved_by, status, expires_at | สคริปต์อ้างได้เฉพาะ approved และยังไม่หมดอายุ |
| `product_offers` | id, product_id, market, product_url, price, currency, commission_rate, availability, checked_at | rate อยู่ 0..1; freshness ตรวจใหม่ก่อน export |
| `assets` | id, sha256, relative_path, kind, mime, bytes, width, height, duration_ms, rights_status, source_reference, cloud_use_allowed | unique sha256+kind; asset binary ไม่แก้ in-place |
| `product_assets` | product_id, asset_id, display_order | unique product_id+asset_id |
| `channels` | id, platform, display_name, market, locale, timezone, status, capabilities_json | ความสามารถ default false; ไม่เก็บ password social |
| `campaigns` | id, product_id, channel_id, mode, locale, target_duration_ms, budget_usd, status | mode template_only/hybrid_motion |
| `content_revisions` | id, campaign_id, revision, product_snapshot_json, script_json, script_hash, prompt_version, model_id, status | unique campaign_id+revision; approved script immutable |
| `scene_assets` | content_revision_id, scene_id, role, asset_id | mapping exact scene ไป immutable asset |
| `video_versions` | id, content_revision_id, version_no, manifest_json, manifest_sha256, output_asset_id, qa_json, status | unique revision+version_no; rerender creates new row |
| `jobs` | id, job_type, payload_json, payload_version, dedupe_key, lane, status, priority, run_after, attempts, max_attempts, lease_owner, lease_until, fence, created_at | unique dedupe_key; partial index on eligible queue |
| `job_attempts` | id, job_id, attempt_no, fence, started_at, ended_at, error_code, trace_id | unique job_id+attempt_no; no secret/raw image bodies |
| `job_events` | id, job_id, event_type, details_json, occurred_at | append-only transition history |
| `outbox_events` | id, event_type, aggregate_id, payload_json, published_at, created_at | enqueue downstream inside same DB transaction |
| `provider_requests` | id, job_id, provider, model, client_operation_id, request_hash, provider_request_id, status_url, response_url, status, submitted_at, last_checked_at | unique client_operation_id; provider ID nullable for UNKNOWN |
| `price_snapshots` | id, provider, model, config_hash, unit, unit_price_usd, source_url, verified_at, valid_until | only manually verified snapshot may authorize paid run |
| `budget_periods` | id, scope_type, scope_id, period_start, period_end, limit_usd, reserved_usd, spent_usd | unique scope+period; lock rows in deterministic order |
| `cost_entries` | id, operation_id, idempotency_key, entry_type, amount_usd, certainty, reservation_id, provider_request_id, price_snapshot_id, created_at | unique idempotency_key; append-only adjustments |
| `approvals` | id, video_version_id, manifest_sha256, reviewer_id, decision, checklist_json, decided_at | binds exact manifest; no approval inheritance to next version |
| `export_packages` | id, video_version_id, channel_id, package_hash, relative_path, created_at | unique video_version_id+channel_id+package_hash |
| `publications` | id, video_version_id, channel_id, post_url, external_post_id, published_at, product_link_status, evidence_reference | export != publication; explicit owner confirmation |
| `metric_snapshots` | id, publication_id, source, as_of, views, product_clicks, orders, commission_pending, commission_settled, currency, import_key | unique import_key; cumulative snapshots not summed blindly |
| `schedules` | id, campaign_id, timezone, local_time, days_of_week, enabled, max_jobs_per_day, last_slot | daily local schedule with IANA timezone |
| `schedule_occurrences` | id, schedule_id, slot_key, job_id, created_at | unique schedule_id+slot_key stops duplicate catch-up |
| `audit_events` | id, actor, action, target_type, target_id, details_json, created_at | append-only; redact sensitive fields |

`cost_entries` เป็น ledger ที่ตรวจสอบย้อนกลับได้ ไม่ใช้ค่าจาก LLM เป็นราคา. `spent_usd` เป็นยอดสำหรับ guard ที่ reconcile กับ ledger. ค่าที่ provider ไม่ยืนยันให้ติด `certainty=estimated` ห้ามแสดงเป็นใบเรียกเก็บเงินจริง

## C3. Queue semantics

Job status:

```text
queued -> running -> succeeded
                 -> retry_wait -> running
                 -> paused
                 -> failed
                 -> cancelled
                 -> blocked
```

Provider request status แยกต่างหาก:

```text
prepared -> submitting -> submitted -> in_progress -> succeeded
                     |                          \-> failed_confirmed
                     \-> submission_unknown
submitted/in_progress -> cancellation_requested -> cancelled_confirmed
```

การจบ job ของขั้น `submit_video` หมายถึงบันทึก request ID แล้ว ไม่ได้หมายความว่าวิดีโอเสร็จ. outbox enqueue `poll_video`; poll ที่ยังไม่เสร็จตั้ง run_after ใหม่โดยไม่ consume failure retry budget

ใช้ `SELECT ... FOR UPDATE SKIP LOCKED` ใน transaction สั้นเพื่อ claim พร้อมอัปเดต lease [S16]. ห้ามทำ network I/O ระหว่างถือ row lock

ค่าเริ่มต้น lease 120 วินาที, heartbeat 20 วินาที, watchdog 30 วินาที. ทุก update/finalize ต้อง `WHERE id=@id AND fence=@fence AND lease_owner=@owner AND lease_until>now()` พร้อมตรวจ affected rows = 1. หากไม่ตรง ถือว่า lost lease ห้าม commit output/state

Fence ต้องเพิ่ม monotonic ทุกครั้งที่ claim/reclaim. cancellation และ local process kill ต้องไม่ปล่อยให้ worker เก่ากลับมาชนะ. ไฟล์ output อยู่ใน workspace ของ attempt ก่อน publish แบบ atomic หลัง fence check เท่านั้น

Pure/local steps retry ได้เมื่อ idempotent. Paid submitting ที่ไม่มีผลลัพธ์แน่ชัด **ไม่** reclaim แล้ว submit ซ้ำ ให้ย้ายไป reconciliation state

## C4. Shared contract files และ signatures

Contract ทั้งหมดอยู่ใน `src/ContentFactory.Contracts/`. ไม่ให้แต่ละ module สร้าง DTO ชื่อคล้ายกันคนละแบบ. แต่ละ record ด้านล่างสร้างเป็นไฟล์เดียวกับชื่อ type; enum รวมที่ `Enums.cs`

```csharp
public enum AssetKind { Image, Audio, Video, Subtitle, Thumbnail, Manifest, Archive }
public enum VideoMode { TemplateOnly, HybridMotion }
public enum JobLane { Control, HeavyLocal, CloudSubmit, CloudPoll, Download }
public enum ProviderRunStatus
{
    Submitted, InProgress, Succeeded, FailedConfirmed,
    SubmissionUnknown, CancellationRequested, CancelledConfirmed
}

public sealed record AssetRef(
    Guid Id, string Sha256, string RelativePath,
    AssetKind Kind, string MimeType, long Bytes, long? DurationMs);

public sealed record ApprovedClaim(Guid Id, string Text, string SourceReference);
public sealed record ProductSnapshot(
    Guid ProductId, long Version, string Title, string Market,
    string Locale, IReadOnlyList<ApprovedClaim> Claims,
    IReadOnlyList<AssetRef> Assets, DateTimeOffset CapturedAt);

public sealed record ScriptRequest(
    Guid RevisionId, ProductSnapshot Product, string Locale,
    VideoMode Mode, int TargetDurationMs, string PromptVersion);

public sealed record ScriptScene(
    string SceneId, string Role, string Narration,
    string Caption, IReadOnlyList<Guid> ClaimIds,
    Guid? PreferredAssetId, string? MotionPrompt);

public sealed record ScriptDocument(
    int SchemaVersion, Guid RevisionId, string Locale,
    string Title, string Disclosure, string CaptionText,
    IReadOnlyList<ScriptScene> Scenes);

public sealed record ValidationIssue(string Code, string Path, string Message);
public sealed record ValidationReport(
    bool Passed, IReadOnlyList<ValidationIssue> Errors,
    IReadOnlyList<ValidationIssue> Warnings);

public sealed record LeasedJob(
    Guid Id, string JobType, string PayloadJson, int PayloadVersion,
    string LeaseOwner, long Fence, DateTimeOffset LeaseUntil);

public sealed record JobOutcome(
    string Status, DateTimeOffset? NextRunAt,
    string? ErrorCode, string? OutputReference);

public sealed record PriceQuote(
    Guid PriceSnapshotId, string Provider, string Model,
    decimal EstimatedUsd, decimal ReservationUsd,
    DateTimeOffset ValidUntil);

public sealed record BudgetReservation(
    Guid Id, string OperationId, decimal ReservedUsd);

public sealed record VideoGenerationRequest(
    string OperationId, Guid RevisionId, string SceneId,
    AssetRef SourceImage, string Prompt, string Resolution,
    string AspectRatio, Guid PriceSnapshotId);

public sealed record ProviderSubmission(
    string Provider, string Model, string RequestId,
    Uri StatusUrl, Uri ResponseUrl, Uri? CancelUrl);

public sealed record ProviderPollResult(
    ProviderRunStatus Status, Uri? OutputUrl,
    string? ErrorCode, int? Seed);

public sealed record VoiceRequest(
    string OperationId, string Locale, string VoiceId,
    string Text, string VoiceConfigHash);

public sealed record VoiceSegment(
    string SceneId, AssetRef Audio, long DurationMs);

public sealed record TimelineSegment(
    string SceneId, long StartMs, long DurationMs,
    AssetRef Visual, AssetRef? Audio, string Caption,
    string Transform);

public sealed record RenderManifest(
    int SchemaVersion, Guid VideoVersionId,
    string ScriptSha256, string TemplateVersion,
    string RendererVersion, string FontFingerprint,
    int Width, int Height, int Fps,
    IReadOnlyList<TimelineSegment> Segments);

public sealed record RenderResult(
    AssetRef Video, AssetRef Thumbnail, AssetRef Subtitles,
    string ManifestSha256, long DurationMs);

public sealed record ReviewDecision(
    Guid VideoVersionId, string ManifestSha256,
    bool Approved, IReadOnlyDictionary<string, bool> Checklist,
    string? Comment);
```

Application interfaces อยู่ `src/ContentFactory.Core/Abstractions/`; methods ทุกตัวส่ง `CancellationToken`:

```csharp
public interface IScriptPlanner
{
    Task<ScriptDocument> GenerateAsync(ScriptRequest request, CancellationToken ct);
}
public interface IScriptValidator
{
    ValidationReport Validate(ScriptDocument script, ProductSnapshot product);
}
public interface IJobHandler
{
    string JobType { get; }
    Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct);
}
public interface IJobStore
{
    Task<LeasedJob?> TryLeaseAsync(string worker, JobLane lane, CancellationToken ct);
    Task<bool> RenewAsync(LeasedJob job, CancellationToken ct);
    Task<bool> FinishAsync(LeasedJob job, JobOutcome outcome, CancellationToken ct);
}
public interface IBudgetGuard
{
    Task<BudgetReservation> ReserveAsync(
        string operationId, Guid campaignId, PriceQuote quote, CancellationToken ct);
    Task SettleAsync(Guid reservationId, decimal amountUsd,
        string certainty, CancellationToken ct);
    Task ReleaseAsync(Guid reservationId, string confirmedReason, CancellationToken ct);
}
public interface IVideoProvider
{
    string Name { get; }
    Task<ProviderSubmission> SubmitAsync(VideoGenerationRequest request, CancellationToken ct);
    Task<ProviderPollResult> PollAsync(ProviderSubmission submission, CancellationToken ct);
    Task RequestCancelAsync(ProviderSubmission submission, CancellationToken ct);
}
public interface IVoiceProvider
{
    Task<AssetRef> SynthesizeAsync(VoiceRequest request, CancellationToken ct);
}
public interface IAssetStore
{
    Task<AssetRef> PutAsync(Stream input, AssetKind kind, string mime, CancellationToken ct);
    Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct);
}
public interface ITimelineCompiler
{
    RenderManifest Compile(Guid videoVersionId, ScriptDocument script,
        IReadOnlyDictionary<string, AssetRef> visuals,
        IReadOnlyList<VoiceSegment> voices, string templateVersion);
}
public interface IVideoRenderer
{
    Task<RenderResult> RenderAsync(RenderManifest manifest, CancellationToken ct);
}
public interface ITechnicalQa
{
    Task<ValidationReport> CheckAsync(RenderResult result, CancellationToken ct);
}
public interface IReviewService
{
    Task RecordAsync(ReviewDecision decision, Guid reviewerId, CancellationToken ct);
}
public interface IExportService
{
    Task<AssetRef> CreateAsync(Guid videoVersionId, Guid channelId, CancellationToken ct);
}
```

Typed exception กลุ่มหลัก: `ValidationException`, `BudgetExceededException`, `PriceStaleException`, `SubmissionUnknownException`, `ProviderRejectedException`, `LostLeaseException`, `DiskPressureException`, `ApprovalStaleException`. API map เป็น ProblemDetails โดยใช้ stable `code`; ไม่ส่ง stacktrace/secret ออก UI

## C5. REST API contract

ทุก endpoint ใต้ `/api/v1` ยกเว้น `/health/live` และ `/health/ready`. Mutating request ต้องผ่าน auth + CSRF. งานที่สร้าง side effect ใช้ `Idempotency-Key` 16–128 ASCII characters และ canonical request hash; same key/different body ตอบ `409 IDEMPOTENCY_CONFLICT`

| Method/path | ผลลัพธ์ | กฎ |
|---|---|---|
| `POST /auth/login` | 204 + cookie | rate limit; ไม่ log password |
| `POST /auth/logout` | 204 | invalidate session |
| `GET /auth/csrf` | 200 `{token}` | ใช้คู่ same-origin cookie |
| `GET /system/status` | 200 health/capabilities | ไม่มี secret values |
| `POST /products` | 201 Product DTO | market/locale/currency validation |
| `POST /products/{id}/claims` | 201 Claim DTO | ต้องมี source reference |
| `POST /products/{id}/assets` | 201 AssetRef | multipart; rights attestation |
| `GET /products/{id}` | 200 | รวม claims/assets/offers |
| `POST /campaigns` | 201 Campaign DTO | snapshot input; ไม่มี paid call |
| `POST /campaigns/{id}/runs` | 202 `{jobId, revisionId}` | validate approved facts + dedupe |
| `GET /jobs/{id}` | 200 state/events | polling progress; no invented percent |
| `POST /jobs/{id}/cancel` | 202 | request cancellation ไม่รับประกัน refund |
| `POST /jobs/{id}/retry` | 202 หรือ 409 | เฉพาะ safe stage; UNKNOWN retry ไม่ได้ |
| `GET /videos/{id}` | 200 VideoVersion DTO | assets, script, QA, cost |
| `GET /assets/{id}/content` | 200/206 stream | signed-in user; range requests |
| `POST /videos/{id}/reviews` | 201 Review DTO | hash+checklist required |
| `POST /videos/{id}/rerenders` | 202 | creates new video version |
| `POST /videos/{id}/exports` | 202 | requires current approval |
| `POST /publications` | 201 | user records real post URL/product link |
| `POST /metrics/imports` | 202 | explicit source column mapping |
| `GET /analytics/overview` | 200 | null when missing, not zero |
| `GET /costs/summary` | 200 | reserved/estimated/confirmed split |
| `POST /schedules` | 201 | disabled by default |
| `PUT /schedules/{id}` | 200 | If-Match version required |
| `POST /system/pause` | 204 | stop new heavy/paid work |
| `POST /system/resume` | 204 | guard still applies |

ตัวอย่างสร้าง run:

```http
POST /api/v1/campaigns/11111111-1111-1111-1111-111111111111/runs
Idempotency-Key: campaign-1111-trial-0001
Content-Type: application/json
X-CSRF-TOKEN: <session-token>

{
  "mode": "template_only",
  "targetDurationMs": 25000,
  "autoGenerateVoice": false,
  "cloudVideoSceneLimit": 0
}
```

ค่า cloud flag เปิดไม่ได้เพียงส่ง body มาเอง: ต้องมี live approval envelope ใน configuration และ server-side budget ผ่านด้วย

---

# ส่วน D — Pipeline และข้อกำหนดความถูกต้อง

## D1. หนึ่ง run ทำอะไรตามลำดับ

1. `validate_product`: ตรวจสิทธิ์สื่อ, claim status, locale และ product offer; freeze `ProductSnapshot`
2. `plan_script`: LM Studio หรือ mock คืน ScriptDocument; validate schema และ claim references
3. `prepare_visuals`: เลือกภาพจริง/คลิปจริง; re-encode สื่อสำหรับ render; ไม่ส่งภาพขึ้น cloud ที่ไม่มี consent
4. `synthesize_voice`: ใช้ uploaded narration หรือ Azure Speech ที่เลือกเปิด; cache ต่อข้อความ/เสียง/config
5. `submit_video`: เฉพาะ hybrid; จองงบ, เตรียม request, เรียก fal หนึ่งครั้ง, บันทึก request ID
6. `poll_video`: ทำงานรอบสั้นโดยมี next-run time; รับ output แล้ว enqueue download
7. `download_video`: validate destination, stream to temp, FFprobe, SHA-256, atomic store
8. `compile_timeline`: ใช้ความยาวเสียงจริงและสื่อที่ได้; คำนวณ subtitle timing เป็นมิลลิวินาที
9. `render_video`: native FFmpeg; log arguments แบบ redacted; process timeout/cancellation
10. `technical_qa`: ตรวจ media จริงด้วย FFprobe/FFmpeg + subtitle bounds
11. `prepare_review`: รวบรวม product facts, script, motion clip, output, cost และข้อเตือน
12. `approve`: ผู้ใช้ตรวจ checklist; approval ผูก manifest hash
13. `export`: สร้าง ZIP/โฟลเดอร์ไฟล์พร้อมเผยแพร่; **ยังไม่ตั้ง published**
14. `record_publication`: ผู้ใช้บันทึกผลการเผยแพร่จริง; analytics import ตามมาภายหลัง

`template_only` ข้ามขั้น 5–7 โดยบันทึก `not_required` ไม่ใช่ fake success จาก Video API. หาก hybrid motion ไม่ผ่าน semantic review ให้ผู้ใช้เลือกใช้ภาพจริงแทน; ไม่เปลี่ยน provider หรือ charge ซ้ำเอง

## D2. Script rules

`schemaVersion=1`, 3–6 scenes; sceneId unique; roles ใช้ `hook`, `benefit`, `demo`, `cta`. ทุกคำกล่าวอ้างเชิงข้อเท็จจริงมี claimIds. hook ที่เป็นคำถามทั่วไปไม่ต้องมี claim แต่ต้องไม่ยืนยันผลสินค้าที่ไม่มีหลักฐาน

`Narration` เป็น plain text, ไม่รับ SSML จาก LLM. `Caption` เป็น plain text ไม่ใช่ HTML/ASS. `MotionPrompt` อธิบายการเคลื่อนกล้องหรือสภาพแวดล้อม ไม่ใช่ให้สินค้ามีคุณสมบัติใหม่. ห้ามนำ LLM output มาเป็น shell command, SQL หรือ filename

Prompt system ต้องระบุให้ product facts เป็นข้อมูลที่ไม่น่าเชื่อถือในเชิงคำสั่ง: ข้อความอย่าง “ignore previous instructions” ใน description ไม่มีอำนาจเปลี่ยนพฤติกรรม. ส่ง facts แบบ JSON ใน message แยกจาก instruction

ไม่พึ่ง JSON-mode ว่าจะถูกต้องเสมอ: parse + schema + business validation ทุกครั้ง [S06]. อนุญาต repair 1 ครั้งด้วยรายการ validation errors; หากยังไม่ผ่าน -> `blocked/SCRIPT_INVALID` ไม่วนไม่สิ้นสุด

วัด token budget ตามโมเดลจริง; ไม่ hardcode model ID จากชื่อที่เดา. UI ต้องแสดง model readiness และขนาด context ที่ตั้งไว้; หาก context ไม่พอให้ลดข้อมูลที่ไม่จำเป็น ไม่เพิ่มเป็น 128K อัตโนมัติ

## D3. เสียงพากย์และ caption ภาษาไทย

**MVP modes:** `uploaded` และ `azure`. ใน mock ใช้ fixture audio ที่สร้างเองหรือมีสิทธิ์ ห้ามถือเสียง sine/silence ว่าเป็นเสียงพากย์จริงในการผ่าน live acceptance

Azure Speech รองรับการขอรายชื่อเสียงตาม endpoint และการสังเคราะห์ผ่าน REST [S17]; voice ID ต้องเลือกจากรายการที่ endpoint นั้นส่งกลับและตรง locale. ไม่ใส่ voice ID ที่ยังไม่ตรวจ ไม่ใช้ unofficial Edge TTS เป็น production dependency

สร้างเสียงแยกเป็นช่วงสั้นตาม caption phrase (เช่น 1–2 บรรทัด) แล้ว FFprobe วัดแต่ละช่วง จึงไม่ต้องพึ่ง speech-to-text เพื่อเดา timestamps. ในหนึ่ง scene มีหลาย phrase ได้ แต่ Task T10 ต้องเก็บ phrase mapping ภายใน voice manifest; รวมกลับเป็น VoiceSegment สำหรับ compiler

กฎ timing: เริ่ม caption พร้อม phrase, ปิดหลังจบ phrase + padding 100ms โดยไม่เกินขอบ scene. รวม narration duration จริง + silence padding; ห้ามตัดคำกลางเสียง. หากเกิน 35 วินาที ให้หยุดด้วย `SCRIPT_TOO_LONG` หรือให้ผู้ใช้อนุมัติการย่อใหม่ก่อนเรียก paid TTS ซ้ำ

Thai layout ใช้ grapheme-aware segmentation และ font ที่มี Thai shaping. ห้าม split ตาม space อย่างเดียวหรือจำนวน bytes. ต้องตรวจความกว้างจริงกับ font renderer; จำนวนบรรทัดเกิน 2 ให้ใช้การแบ่ง phrase ที่จุดเหมาะสม ไม่ลด font จนอ่านไม่ได้

Font ต้องติดตั้งหรือดาวน์โหลดจากแหล่งที่มีสิทธิ์พร้อม license โดย implementer และเก็บ fingerprint ของไฟล์ใน manifest. ชุด handoff นี้ไม่มี font files. ผู้ใช้เปลี่ยน font ถือว่าเป็น render version ใหม่และ approval เดิมใช้ไม่ได้

## D4. Media rendering contract

Output: MP4, 1080×1920, 30fps, H.264, yuv420p, AAC 48kHz stereo, `faststart`. ความยาว 15,000–35,000ms. ประกอบภาพจริงกับ motion โดย fit/pad เป็นหลักเพื่อไม่ crop สินค้าขาด; การขยาย 720p เป็น 1080p ไม่ได้ทำให้รายละเอียดกลับมาเท่าต้นฉบับ 1080p

ใช้ integer frame grid; scene duration ปัดบน frame boundary แล้วคำนวณ cumulative time ใหม่. Audio samples จัดให้สอดคล้อง timeline; total audio/video ต่างกันไม่เกิน 200ms. ค่า bitrate เริ่ม video 6Mbps และ audio 128kbps; เป็น configuration ที่ทดสอบปรับได้ ไม่ใช่ข้อกำหนด TikTok

Safe area ของ **template ภายใน** เริ่ม x=80..880, y=180..1500 บน canvas 1080×1920; ไม่อ้างว่าเป็น safe zone ที่แพลตฟอร์มรับประกัน. preview overlay เปิด/ปิดได้; ทดสอบกับ UI ของช่องจริงก่อนเผยแพร่

Encoder selection:

- ตรวจ `ffmpeg -encoders` ว่ามี `h264_videotoolbox`; ทำ probe encode จริงก่อนเลือก
- หากไม่มี/ล้มเหลว ให้เสนอ CPU `libx264` ที่มีอยู่จริง พร้อม limit threads=4; ไม่อ้าง hardware acceleration จากชื่อเครื่องอย่างเดียว
- `subtitles` filter ต้องมี libass และ font พร้อม; ถ้าไม่พร้อมให้ block พร้อมวิธีแก้ ไม่ข้าม captions เงียบ ๆ
- ใช้ `ProcessStartInfo.ArgumentList`, `UseShellExecute=false`; temporary file names เป็น UUID/hash ไม่ใช่ชื่อจากผู้ใช้
- อนุญาต FFmpeg อ่านเฉพาะไฟล์ local ที่ผ่าน ingest; ไม่ส่ง remote URL จาก LLM ให้ FFmpeg โดยตรง
- process timeout เริ่ม 10 นาทีต่อคลิป; timeout ให้ terminate process tree และเก็บ diagnostic แบบไม่ติด secret

Technical QA ต้องมี MP4 probe success, video/audio streams, dimensions/pixel format/codec, monotonic timestamps, duration bounds, no truncated file, audio presence และ subtitle bounds. Silence ทั้งคลิปถือ fail สำหรับคลิปที่มี narration; black-frame detector เป็น warning ที่ยกเว้น title/end frame ได้ ไม่ใช่ใช้เกณฑ์เดียวกับทุก template

## D5. Budget algorithm

ค่าเสนอเริ่มต้น ไม่ใช่การอนุมัติใช้เงินจริงของผู้ใช้: per request USD 0.20, per finished video USD 0.50, campaign USD 10, day USD 2, month USD 30. `ALLOW_PAID_APIS=false` ชนะทุกค่าเหล่านี้

ขั้นจองงบ:

1. โหลด price snapshot/config เดียวกับ request; หมดอายุ/หน่วยไม่รู้ -> block
2. สร้าง operation ID จาก stage+revision+scene+explicit attempt; ห้ามใช้ timestamp random แก้ dedupe
3. Lock budget rows ตาม scope order `monthly -> daily -> campaign -> video` ทุกครั้ง
4. ตรวจ `spent + reserved + new_reservation <= limit` ของทุก scope และ live approval envelope
5. สร้าง reservation + provider prepared row + job event ใน transaction เดียว; unique operation ID
6. Commit แล้วจึงส่ง network request; reserved = estimate × 1.20 ใน pilot หรือ upper bound ที่ทราบจริง
7. Response สำเร็จ -> บันทึก ID, poll ต่อ; settle ครั้งเดียวตามยอดยืนยัน หรือยอด estimate แบบแสดง uncertainty
8. Failure ที่ยืนยันว่าไม่คิดเงิน -> release ด้วยหลักฐาน. ไม่ทราบ -> hold reservation และ reconcile

งบ guard ช่วยป้องกันการเริ่มงานเกินเพดานที่รู้ แต่ไม่รับประกันยอด invoice ของผู้ให้บริการที่ปรับราคา/คิดเงินล่าช้า. ต้องตั้ง spending controls ของ provider เพิ่มและใช้ safety margin. ยอดเงินจริงเกิน reservation ต้องบันทึกตามจริงและ pause ไม่บังคับเลขลงเพื่อให้ KPI ดูดี

### Retry matrix

| เหตุการณ์ | พฤติกรรม |
|---|---|
| Input/schema invalid ก่อนส่ง | fail หรือให้แก้; ไม่ retry |
| GET status 429 | เคารพ Retry-After + jitter; ไม่มี new submit |
| GET status 5xx/network | bounded exponential retry; ใช้ request เดิม |
| POST ชัดเจนว่า provider ปฏิเสธก่อนรับ เช่น validation | ใช้ documented rejection/billing semantics; แก้ปัญหาก่อน retry |
| POST timeout/reset/5xx หลังเริ่มส่ง | `SUBMISSION_UNKNOWN`; ไม่มี automatic retry |
| 401/403 | pause provider; แจ้ง key/permission โดยไม่ log key |
| Cloud output ไม่ผ่าน QC | manual revision หรือภาพจริง fallback; ไม่จ่ายรอบใหม่เอง |
| Local render fail | retry safe ได้ไม่เกิน 2 ครั้งจาก immutable assets |
| Cancel หลังรับงาน cloud | request cancel; รอ confirm; ไม่สัญญา refund |

Local idempotency ไม่ใช่ provider idempotency และไม่สามารถทำ exactly-once ข้ามเครือข่ายได้ด้วยตัวมันเอง. หาก provider รองรับ idempotency key จริงในอนาคต ให้เพิ่มเฉพาะหลังอ่าน contract และทดสอบแล้ว

## D6. fal adapter — minimum verified request

ใช้ HTTP queue แบบ async และสถานะที่ provider คืน [S13]. เลือก REST ใน .NET เพื่อไม่เพิ่ม Python/Node service สำหรับเพียง adapter เดียว

```json
{
  "image_url": "data:image/jpeg;base64,<normalized-small-input>",
  "prompt": "Subtle camera movement. Preserve the object geometry and labels.",
  "resolution": "720p",
  "aspect_ratio": "9:16",
  "enable_safety_checker": true,
  "enable_output_safety_checker": true,
  "enable_prompt_expansion": false,
  "acceleration": "regular",
  "video_quality": "high",
  "video_write_mode": "balanced"
}
```

Endpoint ID: `fal-ai/wan/v2.2-a14b/image-to-video/turbo`. Submit URL เริ่มจาก `https://queue.fal.run/` + endpoint ID; Authorization ใช้ `Key` ตามเอกสาร. บันทึก `request_id`, `status_url`, `response_url`, `cancel_url` จาก response แทนการเดา prefix status จาก model ID

Normalize input เป็น JPG/PNG สูงสุด 1280px ที่ด้านยาว, ไม่เกิน 3MiB ก่อน base64; strip metadata, ไม่ crop ส่วนสินค้า. ตั้ง request body cap 5MiB. หากเกินให้ block/recompress หรือเพิ่ม staging adapter ที่เจ้าของอนุมัติ ไม่เปิด local storage เป็น public

Result URLs ถือเป็น untrusted input: HTTPS only, approved exact/suffix hosts, ตรวจ DNS/IP ทุก redirect, deny loopback/private/link-local, cap redirects=3, download cap=200MiB, timeout=180s. ไม่มี credentials ของ fal ใน headers ที่ส่งไป storage host

Poll เกิน 30 นาทีให้ `provider_delayed` และแจ้งเตือน ไม่ resubmit. งานยัง reconcile ต่อช้าลงได้. ถ้า Mac sleep provider อาจทำเสร็จไปแล้ว; wake ต้อง query งานเดิมและดาวน์โหลดก่อน URL หมดอายุ

---

# ส่วน E — Security, publishing และข้อมูลผลลัพธ์

## E1. Security model

Local single owner ใช้ ASP.NET cookie auth + PasswordHasher; สร้าง owner ด้วย CLI prompt ไม่ hardcode password. `HttpOnly`, `SameSite=Strict`, anti-forgery token ทุก mutation, strict Host/Origin allowlist, disabled wildcard CORS. มี local HTTPS profile สำหรับการใช้งานจริง; HTTP loopback-only อนุญาตสำหรับพัฒนาเท่านั้นและแสดงคำเตือน

API/UI bind 127.0.0.1 เท่านั้น. PostgreSQL mapping `127.0.0.1:54329:5432`; ไม่มี volume docker socket. Worker ต่อ LM Studio ผ่าน 127.0.0.1 ไม่ต้อง `host.docker.internal` ใน topology นี้. LM Studio authentication เปิดได้ตามรุ่นที่รองรับ และต้องเปิดเมื่อมี API token feature [S07]

Secrets อยู่ environment หรือ OS secret storage; `.env` local permission 0600, ignore ใน git. config example ว่างทั้งหมด. ไม่ scrape credentials จาก OpenCode auth store. API เพียงแสดง `configured=true/false`; secrets ของ fal/TTS อยู่ worker ไม่ใช่ Next.js

Uploads: sniff MIME, file cap image=10MiB/audio=50MiB/video=200MiB, reject SVG/HTML/executable, pixel dimension cap 8192×8192 และ decoded-frame timeout. ปฏิเสธ path traversal และ symlink escape; restore/import ZIP ต้องป้องกัน Zip Slip

Hash พร้อม size เพื่อ identity; สิทธิ์ใช้งานเป็นคนละ field ไม่ถือว่ารูปค้นเจอออนไลน์แปลว่าใช้ทำโฆษณาได้. การส่ง cloud ต้อง `cloud_use_allowed=true` พร้อม audit

## E2. Human review checklist

ต้องยืนยันทั้งหมดก่อน approval:

- สินค้า รูปร่าง สี โลโก้ และการใช้งานไม่ถูก AI เปลี่ยนจนชวนเข้าใจผิด
- คำกล่าวอ้างทั้งหมดตรงหลักฐาน และไม่มี testimonial/การรับรองที่แต่งขึ้น
- เสียงและคำบรรยายอ่านถูก โดยเฉพาะไทยหรือภาษาตลาดปลายทาง
- สิทธิ์ภาพ/เสียง/เพลง/font และการใช้ cloud ได้รับอนุญาต
- disclosure เรื่องโฆษณา/affiliate/AI ที่ต้องใช้ถูกเตรียมไว้สำหรับ workflow นั้น
- product offer/price/stock มีสถานะตรวจแล้วใน 24 ชั่วโมงก่อน export; ถ้าไม่มี live API ผู้ใช้ต้อง refresh/attest เอง
- final MP4 version ตรง preview และ manifest hash ที่อนุมัติ

Automated QA เป็นตัวช่วยไม่ใช่ผู้รับรองความจริงของสินค้า. ห้าม implement `llm_says_safe=true` แล้วข้ามคน review

## E3. Export และ TikTok

ZIP ภายในต้องมี:

```text
video.mp4
thumbnail.jpg
captions.srt
post-caption.txt
product-reference.json
disclosure-checklist.md
manifest.json
sha256sums.txt
```

`product-reference.json` ระบุสินค้า/market/source URL/checked_at ไม่เก็บข้อมูลลูกค้า. `disclosure-checklist.md` แจ้งการตั้ง label ที่ต้องทำบนแพลตฟอร์ม; การใส่บรรทัด disclosure ในไฟล์ไม่ได้เปิด platform label แทนผู้ใช้

TikTok Direct Post guidelines มีข้อจำกัดด้าน intended use รวมถึงเครื่องมือภายในสำหรับบัญชีตนเอง/ทีม และข้อกำหนดความยินยอม [S14]. จึงทำ `ManualExportPublisher` เป็น MVP; ข้อกำหนด Shop/product linking ต้องตรวจแยก ไม่ถือว่า posting endpoint มี `product_id` ที่ใช้ได้ทันที

Future adapter ต้องแสดง capability flags และหลักฐานอนุญาต: `canUploadDraft`, `canDirectPublish`, `canAttachProduct`, `supportsAiDisclosure`. Default false ทุกค่า. ไม่เขียน fake adapter ที่คืน success โดยไม่โพสต์จริง

## E4. Analytics ที่ไม่หลอกตัวเอง

เริ่มด้วย CSV ที่ผู้ใช้ export จากแหล่งที่มีสิทธิ์ และ mapping fields แบบ explicit. ถ้าไม่มีคลิก/ยอดขายให้เก็บ null และแสดง “ไม่มีข้อมูล” ไม่ใช่ 0. วันที่ metric เป็น `as_of` ไม่ใช่วัน import

CTR = productClicks/views เมื่อ denominator > 0 และนิยามแหล่งข้อมูลตรงกัน. CVR = orders/productClicks; ไม่อ้างว่าทุก order เกิดจากวิดีโอหากไม่มี attribution. คอมมิชชันแยก pending/settled/paid/refunded เท่าที่รายงานมี; ไม่บวกยอด cumulative snapshot ทุกครั้ง

เมื่อมีหลายสกุลเงิน ให้แสดงแยกก่อน. KPI `contribution_after_api_cost` ไม่ใช่ net profit เพราะยังไม่รวมค่าแรง/ไฟฟ้า/ค่า subscription/ภาษี. ข้อมูลน้อยให้รายงาน descriptive ไม่ประกาศชนะ A/B test จากตัวอย่างเล็ก

---

# ส่วน F — Repository และ configuration

## F1. โครงสร้างไฟล์ที่ต้องสร้าง

```text
ai-content-factory/
  AGENTS.md
  README.md
  Makefile
  global.json
  Directory.Build.props
  Directory.Packages.props
  ContentFactory.slnx
  .gitignore
  .env.example
  opencode.jsonc
  docs/
    AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md
    START_HERE_DEEPSEEK.md
    progress.md
    decisions.md
    environment-report.md
    operator-runbook.md
    live-test-approval.json.example
    provider-contracts/
      fal-wan-turbo.input.schema.json
      fal-wan-turbo.output.schema.json
      price-snapshot.example.json
  infra/
    compose.yaml
    postgres/init-test-db.sql
  config/
    appsettings.example.json
    templates/product-basic.v1.json
    locales/th-TH.json
  src/
    ContentFactory.Contracts/       # DTO/records ที่ส่วน C กำหนด
    ContentFactory.Core/
      Abstractions/
      Products/
      Campaigns/
      Jobs/
      Billing/
      Review/
    ContentFactory.Infrastructure/
      Persistence/
        AppDbContext.cs
        Migrations/
      Storage/
      Jobs/
      Billing/
      Providers/LmStudio/
      Providers/Fal/
      Providers/Voice/
      Security/
      Media/
      Analytics/
    ContentFactory.Api/
      Program.cs
      Endpoints/
      Middleware/
    ContentFactory.Worker/
      Program.cs
      JobDispatcher.cs
      Handlers/
      Health/
    ContentFactory.Cli/
      Program.cs
      Commands/
  apps/dashboard/
    package.json
    package-lock.json
    src/app/
    src/components/
    src/lib/api/
    tests/
    e2e/
  media/
    prompts/script-planner.v1.txt
    schemas/script-document.v1.schema.json
    templates/
    fixtures/                      # สื่อที่สร้างเอง/มีสิทธิ์เท่านั้น
  tests/
    ContentFactory.UnitTests/
    ContentFactory.IntegrationTests/
    ContentFactory.MediaTests/
  scripts/
    doctor.sh
    dev-start.sh
    dev-stop.sh
    backup.sh
    restore.sh
    verify-all.sh
    verify-no-secrets.sh
  azure-pipelines.yml
```

ไม่ใส่ runtime data ไว้ใน repo. ค่า default `DATA_ROOT=$HOME/Library/Application Support/ContentFactory` และ path resolver ต้อง expand home อย่างชัดเจน ไม่คาดว่า .NET จะแปล `$HOME` ใน JSON เอง

```text
DATA_ROOT/
  assets/sha256/<first-2>/<hash>.<ext>
  temp/<job-id>/<attempt-id>/
  exports/<video-version-id>/<package-hash>/
  logs/
  backups/
  manifests/
```

สำรองลง volume/เครื่องอื่นด้วย; โฟลเดอร์ backups บน SSD เดียวกันไม่ป้องกัน SSD เสีย

## F2. Configuration contract

`.env.example` มีเฉพาะชื่อและค่า default ไม่ใส่ key จริง:

```dotenv
ASPNETCORE_ENVIRONMENT=Development
ASPNETCORE_URLS=http://127.0.0.1:5080
DASHBOARD_HOST=127.0.0.1
DASHBOARD_PORT=3000
DATABASE_PORT=54329
DATABASE_NAME=content_factory
DATABASE_USER=content_factory
DATABASE_PASSWORD=
DATA_ROOT=
TEXT_PROVIDER=mock
LM_STUDIO_BASE_URL=http://127.0.0.1:1234/v1
LM_STUDIO_API_TOKEN=
LM_STUDIO_MODEL_ID=
TEXT_MAX_OUTPUT_TOKENS=2048
VIDEO_PROVIDER=mock
FAL_KEY=
FAL_MODEL_ID=fal-ai/wan/v2.2-a14b/image-to-video/turbo
VIDEO_RESOLUTION=720p
VOICE_PROVIDER=uploaded
AZURE_SPEECH_ENDPOINT=
AZURE_SPEECH_KEY=
AZURE_SPEECH_VOICE_ID=
ALLOW_PAID_APIS=false
LIVE_APPROVAL_FILE=
AUTO_PUBLISH=false
PUBLISHING_MODE=manual_export
DAILY_BUDGET_USD=2.00
MONTHLY_BUDGET_USD=30.00
MAX_VIDEO_COST_USD=0.50
MAX_SINGLE_REQUEST_USD=0.20
LLM_CONCURRENCY=1
RENDER_CONCURRENCY=1
VIDEO_API_INFLIGHT=2
QUIET_MODE=true
ENABLE_SCHEDULES=false
MIN_FREE_DISK_GIB=20
MIN_FREE_DISK_RATIO=0.10
```

เอกสารนี้ **ไม่ให้** เก็บ `OPENCODE_GO_KEY` เป็น runtime secret เพราะไม่ได้ใช้ Go ใน production pipeline. Authentication ของ coding agent ให้ผู้ใช้ทำผ่าน OpenCode `/connect` เอง

`ALLOW_PAID_APIS=true` เพียงอย่างเดียวไม่พอ: ต้องมี approval file พร้อม scope/provider/model/expiry/maxRequestCount/maxTotalUsd. CLI แสดงยอดและให้ผู้ใช้ยืนยันก่อนบันทึก file. Worker ตรวจทุก submit ไม่ใช่เฉพาะตอน start

ตัวอย่าง approval file สำหรับเจ้าของแก้และอนุมัติเอง ไม่ใช่การอนุมัติที่เกิดขึ้นแล้ว:

```json
{
  "approvalId": "owner-defined-id",
  "provider": "fal",
  "model": "fal-ai/wan/v2.2-a14b/image-to-video/turbo",
  "maxRequestCount": 10,
  "maxTotalUsd": 1.20,
  "expiresAt": "<owner-supplied-ISO-8601-future-time>",
  "allowFallbackProvider": false,
  "approvedBy": "<owner>"
}
```

Placeholder ที่เป็น credential/เวลาอนุมัติใน example ต้องทำให้ validation fail จนผู้ใช้กรอกจริง. สิ่งนี้ไม่ใช่ TODO ที่ agent ไปเดาค่าแทนได้

## F3. Commands ที่ต้องมีเมื่อ milestone ที่เกี่ยวข้องเสร็จ

```bash
make doctor                 # ตรวจ prerequisites แบบไม่แก้เครื่อง
make infra-up               # PostgreSQL only; no destructive reset
make migrate                # explicit app DB migration
make owner-init             # interactive password; never echo
make dev                    # API + worker + UI on loopback
make test-unit
make test-integration       # test DB only
make test-media             # requires native FFmpeg
make test-e2e               # mock providers; no paid traffic
make verify                # lint + build + tests + secret scan
make smoke-mock             # one full fixture -> reviewed export flow
make backup                 # quiesce + verified backup
make restore-test           # isolated restore, never live DB
make benchmark-local        # no cloud API
make live-preflight         # show price/config/approval; no generation
make live-pilot             # only explicit live envelope permits requests
```

ห้ามให้ `make test`, build hook หรือ UI startup เรียก paid provider. คำสั่ง doctor/health/readiness ต้องไม่ส่ง generation request เพื่อทดสอบว่า key ใช้ได้

---

# ส่วน G — แผนพัฒนาเป็นงานย่อยสำหรับ DeepSeek

## G0. วิธีเดินงาน

ทำตาม dependencies และหยุดที่ milestone gate; ไม่ทำทุก task ใน context เดียว. งานแต่ละชิ้นต้องผ่าน RED → GREEN → review → progress update → commit. ไม่ commit secrets หรือ generated media. หากไม่มี git user configuration ให้รายงานและเก็บ changes ไว้ ไม่สร้างตัวตน Git ปลอม

| Milestone | Tasks | สิ่งที่ต้องสาธิต |
|---|---|---|
| M0 Foundation | T00–T05 | เพิ่มสินค้า/สื่อและสร้าง immutable campaign revision ได้ |
| M1 Durable production core | T06–T12 | mock/local script + narration + native MP4 ผ่าน QA |
| M2 Cloud integration | T13–T15 | fake fal และ optional approved live fal ผ่าน adapter เดียวกัน |
| M3 Operator workflow | T16–T19 | review, export, schedule, metrics import ใช้งานได้ |
| M4 Reliability/release | T20–T23 | restart/restore/guards/benchmark/CI มีหลักฐาน |

งานใน M0 เริ่มได้โดยไม่มี API key; งาน T13–T15 พัฒนาให้ครบด้วย fake HTTP server ก่อน. Live pilot ไม่ใช่เงื่อนไขบังคับของการเขียนโค้ดสำเร็จ แต่ต้องผ่านก่อนใช้จริงกับสินค้าหรือเงินจริงต่อเนื่อง

---

## T00 — ตรวจเครื่องและสร้าง repository foundation

**Depends:** ไม่มี

**Files:** Create `.env.example`, `.node-version`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `ContentFactory.slnx`, `.gitignore`, `Makefile`, `scripts/doctor.sh`, `tests/ContentFactory.UnitTests/RepositoryBootstrapTests.cs`, `docs/environment-report.md`

**Interfaces:** Produces .NET project references ตาม F1 และคำสั่ง `make doctor`, `make test-unit`; doctor คืน exit 0 เมื่อพร้อม, 2 เมื่อ prerequisites ขาด, 1 เมื่อ internal error

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Contains("ALLOW_PAID_APIS=false", envExample);
Assert.Contains("AUTO_PUBLISH=false", envExample);
```

- [ ] สร้าง solution/project/test skeleton ขั้นต่ำ และ failing test `RepositoryContainsRequiredSafetyDefaults`: ตรวจ .env.example ต้องมี `ALLOW_PAID_APIS=false`, ไม่มี secret จริง, worker แยกจาก API
- [ ] รัน `dotnet test tests/ContentFactory.UnitTests --filter RepositoryBootstrapTests`; ต้อง fail จาก config ที่ยังไม่มี ไม่ใช่ปัญหา environment ที่ปิดบังไว้
- [ ] เขียน doctor ตรวจ `uname -m`, `sw_vers`, `sysctl -n hw.memsize`, `dotnet --info`, `node --version`, `docker version`, `ffmpeg -version`, `ffprobe -version`, พื้นที่ disk และ ports; ไม่ส่ง serial หรือ username path ทั้งหมดไป cloud logs
- [ ] Pin SDK .NET 10 patch จริง, Node 24 patch ใน `.node-version`, package dependencies ที่เข้ากันได้; package lock ห้ามลอย. ใช้ Next16 stable patch ตาม support policy [S04][S05]
- [ ] รัน test เดิมและ `make doctor`; บันทึกผลจริง/สิ่งที่ขาด. ไม่มี auto install/sudo โดยไม่ได้รับอนุญาต
- [ ] Commit `chore: bootstrap local content factory workspace` และอัปเดต progress โดยไม่ claim benchmark

**Gate:** ขาด FFmpeg/LM Studio ยังพัฒนา pure unit tests ได้ แต่ media/live gate ต้องระบุ blocked อย่างตรงไปตรงมา

## T01 — Shared contracts และ PostgreSQL schema

**Depends:** T00

**Files:** Create `src/ContentFactory.Contracts/*.cs` ตาม C4, `src/ContentFactory.Core/Abstractions/*.cs`, `src/ContentFactory.Infrastructure/Persistence/AppDbContext.cs`, `infra/compose.yaml`, `src/ContentFactory.Infrastructure/Persistence/Migrations/*`, `tests/ContentFactory.IntegrationTests/SchemaTests.cs`

**Interfaces:** Consumes C4 types; Produces `AppDbContext` และ schema ใน C2. DTO JSON ใช้ camelCase; enum serialize เป็น string

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(0.100001m, reloadedAmount);
Assert.Equal(originalInstant, reloadedInstant);
Assert.NotNull(duplicateInsertException);
```

- [ ] เขียน `DuplicateDedupeKeyRejected`, `MoneyRoundTripsSixDecimals`, `InvalidCommissionRateRejected`, `TimestampRetainsInstant`; seed ทดสอบใช้ database `cf_test_<runid>` เท่านั้น
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter SchemaTests`; ต้อง fail เพราะ migration/constraints ยังไม่ครบ
- [ ] สร้าง schema และ FKs/index/check constraints; เริ่ม migration แรกสำหรับ M0/M1 และเพิ่ม tables ตาม module ภายหลังได้ แต่ชื่อ/contract ต้องตรง C2
- [ ] Implement enum/DTO serialization tests; ทำ compose bind loopback และ volume ไม่ใช้ `down -v` ใน make ปกติ
- [ ] รัน migration บน DB ใหม่และ DB ที่ migrate แล้ว; tests ต้องผ่านโดยไม่ drop app database
- [ ] Commit `feat: add shared contracts and durable data model`

**Gate:** `make infra-up && make migrate` สำเร็จ; unique constraints บังคับใน DB ไม่ใช่ application-only

## T02 — Local owner authentication และ secret isolation

**Depends:** T01

**Files:** Create `src/ContentFactory.Cli/Commands/InitOwnerCommand.cs`, `src/ContentFactory.Api/Endpoints/AuthEndpoints.cs`, `src/ContentFactory.Api/Middleware/LocalRequestGuard.cs`, `src/ContentFactory.Infrastructure/Security/SecretRedactor.cs`, `tests/ContentFactory.IntegrationTests/AuthenticationTests.cs`

**Interfaces:** Produces auth routes C5, authenticated owner GUID และ sanitized log utility; worker ไม่ต้องมี browser session

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(HttpStatusCode.Unauthorized, anonymousProductPost.StatusCode);
Assert.DoesNotContain(secretValue, capturedLogs);
```

- [ ] เขียน `AnonymousMutationReturns401`, `MissingCsrfRejected`, `ForeignOriginRejected`, `InvalidHostRejected`, `SecretsNeverAppearInLogs`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter AuthenticationTests`; ต้องเห็น failing authorization assertions
- [ ] Implement cookie auth, PasswordHasher, CSRF และ loopback-only startup validation; CLI init-owner รับ password แบบ hidden prompt
- [ ] ทำ login throttling และ session logout; ไม่เปิด user registration; ไม่บันทึก password request body
- [ ] รัน tests และตรวจ dashboard proxy ส่ง cookie/CSRF ผ่าน same-origin โดยไม่ปล่อย wildcard CORS
- [ ] Commit `feat: secure local owner access and redact credentials`

**Gate:** local app ไม่พึ่งการเดาว่า localhost ปลอดภัยเสมอ; paid keys ไม่ถูก serialize ไป client

## T03 — Asset ingest, rights และ content-addressed storage

**Depends:** T01–T02

**Files:** Create `src/ContentFactory.Infrastructure/Storage/FileSystemAssetStore.cs`, `src/ContentFactory.Infrastructure/Storage/AssetIngestService.cs`, `src/ContentFactory.Api/Endpoints/AssetEndpoints.cs`, `tests/ContentFactory.UnitTests/AssetPathTests.cs`, `tests/ContentFactory.IntegrationTests/AssetIngestTests.cs`

**Interfaces:** Implements `IAssetStore`; Produces authenticated stream endpoint และ AssetRef ที่ใช้ได้กับทุก provider/render stage

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.False(traversalUpload.Accepted);
Assert.False(disguisedSvgUpload.Accepted);
Assert.True(thaiNameUpload.Accepted);
```

- [ ] เขียน `TraversalAndSymlinkEscapeRejected`, `ThaiFilenameDoesNotBecomeDiskPath`, `SvgDisguisedAsJpegRejected`, `OversizedUploadRejected`, `RightsRequiredForProduction`
- [ ] รัน tests filter `AssetPathTests|AssetIngestTests` โดยใช้ syntax filter ที่ test runner รองรับ; บันทึก RED
- [ ] Implement streamed write -> temp -> hash -> atomic rename -> metadata; sanitize ไม่พึ่งนามสกุล. เก็บสิทธิ์/source/cloud consent แยกจาก file hash
- [ ] เพิ่ม range requests สำหรับ preview; cleanup orphan temp ต้องไม่แตะ assets ที่ DB อ้างถึง
- [ ] รัน tests และ upload ไฟล์ชื่อไทย/เว้นวรรค/quote/semicolon; ต้องยังอ่านได้และไม่มี command execution
- [ ] Commit `feat: ingest licensed media into immutable local storage`

**Gate:** assets ไม่มี public URL และไม่มีสื่อ upload ถูกส่ง cloud โดยอัตโนมัติ

## T04 — Process runner และ manifest identity

**Depends:** T00,T03

**Files:** Create `src/ContentFactory.Infrastructure/Media/SafeProcessRunner.cs`, `src/ContentFactory.Infrastructure/Media/ManifestHasher.cs`, `tests/ContentFactory.UnitTests/SafeProcessTests.cs`, `tests/ContentFactory.UnitTests/ManifestHashTests.cs`

**Interfaces:** Produces `RunAsync(string executable, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)` returning exit code/stdout/stderr; Produces `Compute(RenderManifest manifest): string`

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(hashOfOriginal, hashOfReorderedKeys);
Assert.NotEqual(hashOfOriginal, hashWithEditedCaption);
Assert.Equal(inputWithMetacharacters, capturedSingleArgument);
```

- [ ] เขียน `ShellMetacharactersRemainArguments`, `CancellationStopsChildProcess`, `SameManifestHasSameHash`, `ChangedCaptionChangesHash`, `KeyOrderDoesNotChangeHash`
- [ ] รัน `dotnet test tests/ContentFactory.UnitTests --filter "FullyQualifiedName~SafeProcessTests|FullyQualifiedName~ManifestHashTests"`; ต้อง fail ก่อน implementation
- [ ] Implement ArgumentList/no shell, cancellation/timeout, allowlisted executables และ output log caps; canonical JSON hash โดยเรียง object keys แต่คง array order
- [ ] ใช้ UTF-8 และ invariant number/date representation; ไม่มี current time อยู่ใน hash เว้นแต่เป็นข้อมูล input ที่มีความหมายจริง
- [ ] รัน tests และตรวจว่า error ไม่เปิดเผย temp input content หรือ secret env
- [ ] Commit `feat: add safe media process execution and manifest hashing`

**Gate:** render มี reproducible input identity ไม่ได้อ้าง MP4 byte-identical ทุกเครื่อง

## T05 — Product/campaign snapshot และ enqueue API

**Depends:** T01–T04

**Files:** Create `src/ContentFactory.Core/Products/ProductSnapshotBuilder.cs`, `src/ContentFactory.Api/Endpoints/ProductEndpoints.cs`, `src/ContentFactory.Api/Endpoints/CampaignEndpoints.cs`, `src/ContentFactory.Infrastructure/Jobs/RunEnqueuer.cs`, `tests/ContentFactory.IntegrationTests/CampaignRunTests.cs`

**Interfaces:** Consumes ProductSnapshot/ScriptRequest; Produces POST campaigns/runs ตาม C5 และ immutable content revision + first job ใน transaction เดียว

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(firstRun.JobId, repeatedRun.JobId);
Assert.Equal(HttpStatusCode.Conflict, changedBodyResponse.StatusCode);
Assert.Equal(originalSnapshotHash, previousRevisionSnapshotHash);
```

- [ ] เขียน `DuplicateRequestReturnsSameJob`, `SameKeyDifferentBodyReturns409`, `UnapprovedClaimsBlockRun`, `ProductEditDoesNotMutateOldRevision`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter CampaignRunTests`; verify RED
- [ ] Implement product/claim/offer CRUD เท่าที่จำเป็น, snapshot builder และ idempotent run enqueue; จำกัด single run ต่อคำขอ ไม่ fan out paid scenes ตาม LLM
- [ ] Validate media rights/market/locale; ไม่เชื่อ client-supplied snapshot/price claim ที่ไม่ได้อ่านจาก DB
- [ ] รัน tests และ API smoke เพื่อสร้าง fixture product/campaign โดยไม่มี network AI traffic
- [ ] Commit `feat: create versioned campaign runs from approved product facts`

**M0 Gate:** เพิ่มสินค้าและสร้าง run ได้ 1 ครั้ง แม้กดซ้ำ; screenshot/API evidence บันทึกใน progress โดยไม่ปลอมสถานะผลิตเสร็จ

---

## T06 — Durable job leasing, fencing และ transactional outbox

**Depends:** T01,T05

**Files:** Create `src/ContentFactory.Infrastructure/Jobs/PostgresJobStore.cs`, `src/ContentFactory.Infrastructure/Jobs/OutboxDispatcher.cs`, `src/ContentFactory.Infrastructure/Jobs/LeaseWatchdog.cs`, `tests/ContentFactory.IntegrationTests/JobLeasingTests.cs`

**Interfaces:** Implements `IJobStore`; outbox handler produces deduplicated downstream jobs by event ID

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.False(await store.FinishAsync(expiredLease, outcome, ct));
Assert.Equal(100, distinctSuccessfulJobIds.Count);
Assert.Equal(1, nextJobsForOneOutboxEvent.Count);
```

- [ ] เขียน `TwoWorkersNeverOwnSameFence`, `ExpiredWorkerCannotFinalize`, `HeartbeatExtendsLease`, `OutboxReplayCreatesOneNextJob`, `RollbackCreatesNeitherRevisionNorJob`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter JobLeasingTests` บน PostgreSQL จริง; in-memory DB ใช้พิสูจน์ locking ไม่ได้
- [ ] Implement claim+lease+fence ใน transaction สั้นด้วย SKIP LOCKED; partial index status/run_after/priority/created_at
- [ ] Implement heartbeat/watchdog/outbox dispatcher; provider submission ที่ไม่แน่นอนห้ามกลับไป queued เพื่อ submit อีก
- [ ] รัน worker 2 instances กับ 100 fake jobs; assert 100 unique finalizations และ old fences อัปเดตไม่ได้
- [ ] Commit `feat: implement durable jobs with fencing and outbox recovery`

**Gate:** ไม่อ้าง exactly-once provider execution; รับรองเฉพาะ invariants ที่ทดสอบได้ใน DB

## T07 — Worker orchestration, pause/resume และ resource lanes

**Depends:** T06

**Files:** Create `src/ContentFactory.Worker/JobDispatcher.cs`, `src/ContentFactory.Worker/Handlers/`, `src/ContentFactory.Worker/Health/ResourceGovernor.cs`, `src/ContentFactory.Core/Jobs/WorkflowDefinition.cs`, `tests/ContentFactory.UnitTests/WorkflowTests.cs`, `tests/ContentFactory.IntegrationTests/WorkerRecoveryTests.cs`

**Interfaces:** Consumes `IJobHandler`, `IJobStore`; Produces ordered stages D1 and control endpoints pause/resume

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(0, cloudSubmitCountForTemplateMode);
Assert.Equal(1, maximumObservedHeavyLocalConcurrency);
Assert.Equal(attemptsBeforePoll, attemptsAfterInProgressPoll);
```

- [ ] เขียน `TemplateModeSkipsCloudStages`, `PausePreventsNewHeavyWork`, `SleepRecoveryDoesNotFanOut`, `LostLeaseCancelsLocalProcess`, `PollRescheduleDoesNotConsumeRetryLimit`
- [ ] รัน tests `WorkflowTests` และ `WorkerRecoveryTests`; RED ต้องพิสูจน์ missing transitions ไม่ใช่ใช้ sleep แบบ flaky
- [ ] Implement stage DAG ที่เป็น configuration/code ชัดเจน ไม่ให้ LLM สร้าง arbitrary workflow; child event สร้างใน transaction ของ finalize
- [ ] ใช้ lane semaphore และ DB-backed cloud in-flight count; quiet mode serializes render กับ local inference. Memory/thermal sensor ไม่รองรับให้รายงาน unavailable ไม่เดาตัวเลข
- [ ] Restart worker กลาง fake step แล้ว verify state recovery; graceful shutdown หยุด lease ใหม่และ cancel local processes ภายใน 30 วินาที
- [ ] Commit `feat: orchestrate resumable workflows with resource controls`

**Gate:** cloud waiting ไม่ยึด heavy-local slot; ปุ่ม pause ไม่ใช่คำสั่ง refund หรือ kill ทุก cloud request

## T08 — LM Studio script planner และ strict validation

**Depends:** T04,T05,T07

**Files:** Create `src/ContentFactory.Infrastructure/Providers/LmStudio/LmStudioScriptPlanner.cs`, `src/ContentFactory.Core/Campaigns/ScriptValidator.cs`, `media/prompts/script-planner.v1.txt`, `media/schemas/script-document.v1.schema.json`, `tests/ContentFactory.UnitTests/ScriptValidationTests.cs`, `tests/ContentFactory.IntegrationTests/PlannerAdapterTests.cs`

**Interfaces:** Implements `IScriptPlanner`, `IScriptValidator`; uses ScriptRequest -> ScriptDocument; mock planner implements same interface

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.False(validator.Validate(scriptWithUnknownClaim, product).Passed);
Assert.InRange(repairRequestCount, 0, 1);
Assert.Equal(configuredModelId, capturedRequestModelId);
```

- [ ] เขียน `UnknownClaimIdRejected`, `PromptInjectionTreatedAsData`, `DuplicateSceneIdsRejected`, `MalformedJsonRepairedAtMostOnce`, `LocalModelUnavailableReturnsBlocked`
- [ ] รัน `dotnet test tests/ContentFactory.UnitTests --filter ScriptValidationTests` และ adapter tests fake HTTP; verify RED
- [ ] Implement structured output call `/v1/chat/completions`; model ID/config จาก settings เท่านั้น. Handle JSON-string content, provider refusal, truncated finish_reason และ timeout
- [ ] จำกัด 3–6 scenes/one motion scene; ไม่ส่ง tools/MCP หรือ client secrets ไป LLM. Store prompt version/model ID/token usage เท่าที่ response มี
- [ ] รัน tests; optional local smoke กับสินค้า fixture ที่ไม่มีข้อมูลส่วนตัวและโมเดลที่ติดตั้งอยู่แล้ว ไม่มี model download อัตโนมัติ
- [ ] Commit `feat: generate validated scripts with a local model adapter`

**Gate:** ไม่มี unknown product claim และ schema-invalid script หลุดไปจ่าย TTS/Video API

## T09 — Budget reservations, cost ledger และ approval envelope

**Depends:** T01,T06

**Files:** Create `src/ContentFactory.Infrastructure/Billing/PostgresBudgetGuard.cs`, `src/ContentFactory.Infrastructure/Billing/PriceSnapshotService.cs`, `src/ContentFactory.Infrastructure/Billing/LiveApprovalGuard.cs`, `tests/ContentFactory.IntegrationTests/BudgetRaceTests.cs`, `tests/ContentFactory.UnitTests/LiveApprovalTests.cs`

**Interfaces:** Implements `IBudgetGuard`; provides `GetQuoteAsync(provider, model, configHash, ct): Task<PriceQuote>`

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(8, successfulReservations.Count);
Assert.Equal(0.96m, successfulReservations.Sum(x => x.ReservedUsd));
Assert.Equal(reservedBeforeTimeout, reservedAfterUnknownSubmission);
```

- [ ] เขียน `ConcurrentReservationsCannotExceedLimit`, `SettlementIsIdempotent`, `UnknownSubmissionKeepsReservation`, `StalePriceBlocksSpend`, `PaidDisabledBlocksEveryProvider`, `ApprovalExpiresDuringBatch`
- [ ] รัน budget tests บน real PostgreSQL; ตรวจว่าทั้ง daily/monthly/campaign/video caps ถูกทดสอบ ไม่ใช่แค่ per-request
- [ ] Implement lock order และ unique operation; เก็บ snapshot reference ในทุก paid operation. ใช้ decimal ไม่ใช้ float/double
- [ ] Implement optional confirmed refund/adjustment entries; unknown bill ไม่ถูกลดเป็น 0. แยก API spend จาก Go coding subscription
- [ ] ยิง reserve พร้อมกัน 20 requests ใน test งบ USD 1.00 โดย quote reservation USD 0.12: ต้องอนุมัติไม่เกิน 8 และไม่ยอดติดลบเมื่อ release/settle ซ้ำ
- [ ] Commit `feat: enforce atomic spending controls and immutable cost records`

**Gate:** live call ที่ bypass guard ต้องถูก test ปิดทางทั้งหมด

## T10 — Narration adapter และ phrase-level timing

**Depends:** T03,T04,T08,T09

**Files:** Create `src/ContentFactory.Infrastructure/Providers/Voice/UploadedVoiceProvider.cs`, `src/ContentFactory.Infrastructure/Providers/Voice/AzureSpeechVoiceProvider.cs`, `src/ContentFactory.Infrastructure/Media/VoiceAssembler.cs`, `tests/ContentFactory.UnitTests/VoiceManifestTests.cs`, `tests/ContentFactory.IntegrationTests/VoiceProviderTests.cs`

**Interfaces:** Implements `IVoiceProvider`; UploadedVoiceProvider resolves uploaded asset from scene/operation mapping; VoiceAssembler produces IReadOnlyList<VoiceSegment> plus phrase timings in asset manifest

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(measuredAudioDurationMs, voiceSegment.DurationMs);
Assert.Equal(1, billableRequestsForSameVoiceCacheKey);
Assert.False(missingNarrationResult.Passed);
```

- [ ] เขียน `MissingNarrationNeverBecomesSilentSuccess`, `VoiceLocaleMismatchRejected`, `SsmlFromModelIsEscaped`, `CachedVoiceDoesNotChargeAgain`, `ActualAudioDurationDefinesCaptionTiming`
- [ ] รัน tests ด้วย recorded fixtures/fake HTTP; no live Azure calls
- [ ] Implement uploaded mode และ Azure REST mode; fetch allowed voices through configured endpoint; build SSML ด้วย XML writer ไม่ concatenate untrusted XML
- [ ] Cache key = text hash + voice ID + locale + synthesis config/provider version; นำผ่าน budget guard แบบเดียวกับ video เมื่อใช้ paid TTS. ไม่มีราคา speech ที่ตรวจแล้วให้ block live mode
- [ ] FFprobe ทุก segment; reject corrupt/empty/silent narration; demo tone fixture ผ่านเฉพาะ test ที่ระบุ media-synthetic ไม่ผ่าน production voice review
- [ ] Commit `feat: add auditable narration generation and timing manifests`

**Gate:** ระบบ template_only ผลิตจริงได้ด้วย uploaded voice โดยไม่บังคับซื้อ TTS; full voice automation ต้องเปิด provider ที่ตรวจสิทธิ์และงบแล้ว

## T11 — Timeline compiler, Thai captions และ native FFmpeg renderer

**Depends:** T03,T04,T08,T10

**Files:** Create `src/ContentFactory.Infrastructure/Media/TimelineCompiler.cs`, `src/ContentFactory.Infrastructure/Media/CaptionLayout.cs`, `src/ContentFactory.Infrastructure/Media/FfmpegVideoRenderer.cs`, `src/ContentFactory.Infrastructure/Media/EncoderProbe.cs`, `tests/ContentFactory.MediaTests/RenderGoldenTests.cs`, `tests/ContentFactory.UnitTests/TimelineTests.cs`

**Interfaces:** Implements `ITimelineCompiler`, `IVideoRenderer`; produces RenderManifest/RenderResult matching C4 and D4

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(1080, manifest.Width);
Assert.Equal(1920, manifest.Height);
Assert.Equal(30, manifest.Fps);
Assert.False(captionOverflowReport.Passed);
```

- [ ] เขียน `ThaiMarksStayWithBaseCharacter`, `AssOverrideTextRenderedLiterally`, `CaptionOverflowBlocksRender`, `NarrationNeverCutMidWord`, `TimelineDurationUsesFrameGrid`, `MissingHardwareEncoderFallsBackExplicitly`
- [ ] รัน unit tests และ `dotnet test tests/ContentFactory.MediaTests --filter RenderGoldenTests`; ขาด FFmpeg/font ต้องรายงาน skipped/blocked ไม่ใช่ pass
- [ ] Implement product-basic template: image pan/zoom, optional one motion asset, narration, subtitles และ CTA. ทั้ง filter graph และ ASS text escape ผ่าน utility ที่ทดสอบแล้ว
- [ ] Probe encoder/filter/font, render ผ่าน SafeProcessRunner, `faststart`, atomic asset registration; thumbnail จาก frame ที่กำหนดใน template ไม่จับ black first frame โดยไม่ตรวจ
- [ ] สร้าง 3 fixture clips ไทย/อังกฤษ/ข้อความยาว; ตรวจ frame samples, captions และเสียง. Golden image ใช้ tolerance ไม่ hash MP4 ทั้งไฟล์
- [ ] Commit `feat: render vertical product videos with native media tooling`

**Gate:** ได้ MP4 แนวตั้งจริง ไม่ใช่ placeholder PNG เปลี่ยนนามสกุล; ไม่มี clip ที่ตัดคำ/ซับล้นถูกส่งผ่าน

## T12 — Technical QA และ review-ready packaging

**Depends:** T07,T11

**Files:** Create `src/ContentFactory.Infrastructure/Media/TechnicalQa.cs`, `src/ContentFactory.Worker/Handlers/TechnicalQaHandler.cs`, `tests/ContentFactory.MediaTests/TechnicalQaTests.cs`, `tests/ContentFactory.IntegrationTests/ReviewReadyTests.cs`

**Interfaces:** Implements `ITechnicalQa`; Produces ValidationReport และ state `awaiting_review` เท่านั้นเมื่อผ่าน technical constraints

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.False(corruptMp4Report.Passed);
Assert.False(missingRequiredAudioReport.Passed);
Assert.True(validNarratedClipReport.Passed);
```

- [ ] เขียน `CorruptMp4Rejected`, `MissingAudioRejectedWhenNarrationExpected`, `WrongDimensionsRejected`, `DurationOutsideBoundsRejected`, `IntentionalEndCardDoesNotTriggerWholeClipFailure`
- [ ] รัน `dotnet test tests/ContentFactory.MediaTests --filter TechnicalQaTests`; บันทึก failing fixtures
- [ ] Implement stream metadata + bounded frame/audio checks; warnings ไม่ถูกแปลงเป็น confidence score 100% จาก LLM
- [ ] เก็บ structured qa_json และ readable issues; invalid output ยังคงอยู่เพื่อ debug ภายใต้ retention ไม่แอบลบทิ้งแล้วสร้างสำเร็จปลอม
- [ ] ทดสอบ pipeline template_only จน awaiting_review ด้วย mock planner + uploaded licensed narration
- [ ] Commit `feat: validate final media before owner review`

**M1 Gate:** หนึ่ง run ทำงานจากสินค้าไป MP4 จริงพร้อม QA โดยไม่พึ่ง Cloud Video API

---

## T13 — fal submit adapter พร้อม uncertain-submission protection

**Depends:** T03,T07,T09

**Files:** Create `src/ContentFactory.Infrastructure/Providers/Fal/FalVideoProvider.cs`, `src/ContentFactory.Infrastructure/Providers/Fal/FalRequestBuilder.cs`, `src/ContentFactory.Worker/Handlers/SubmitVideoHandler.cs`, `docs/provider-contracts/fal-wan-turbo.input.schema.json`, `tests/ContentFactory.IntegrationTests/FalSubmitTests.cs`

**Interfaces:** Implements `IVideoProvider.SubmitAsync`; consumes VideoGenerationRequest, PriceQuote, BudgetReservation; produces ProviderSubmission persisted before next stage

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(1, fakeProvider.AcceptedPostCount);
Assert.Equal(ProviderRunStatus.SubmissionUnknown, recordedStatus);
Assert.DoesNotContain("duration", capturedJsonPropertyNames);
```

- [ ] เขียน `OnlySchemaFieldsAreSent`, `DurationParameterIsNeverInvented`, `SmallImageUsesDataUri`, `CloudConsentRequired`, `TimeoutAfterAcceptanceIsUnknown`, `ProviderKeyNeverReachesBrowser`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter FalSubmitTests` กับ fake fal server ที่รับ request แล้วตัด connection ก่อนตอบ
- [ ] ดาวน์โหลด schema จาก official URL เพื่อบันทึก version/hash โดยไม่เรียก inference; implement exact D6 request, allowlisted endpoint, safety flags true
- [ ] Persist prepared attempt และ reservation ก่อนส่ง POST; เมื่อ response ไม่แน่ชัด throw SubmissionUnknownException แล้ว hold budget. ไม่ใส่ generic HTTP retry policy ครอบ POST
- [ ] ทดสอบ replay job/restart handler แล้ว verify fake provider รับเพียง 1 POST จน owner reconcile
- [ ] Commit `feat: submit bounded cloud video requests without blind retries`

**Gate:** การ implement สำเร็จไม่ต้องมี FAL_KEY; live mode ยังปิดอยู่

## T14 — Poll, safe download, reconciliation และ cancellation

**Depends:** T06,T13

**Files:** Create `src/ContentFactory.Worker/Handlers/PollVideoHandler.cs`, `src/ContentFactory.Worker/Handlers/DownloadVideoHandler.cs`, `src/ContentFactory.Infrastructure/Providers/Fal/ProviderReconciler.cs`, `src/ContentFactory.Infrastructure/Security/SafeMediaDownloader.cs`, `tests/ContentFactory.IntegrationTests/ProviderRecoveryTests.cs`

**Interfaces:** Implements PollAsync/RequestCancelAsync; produces immutable motion AssetRef และ next render jobs

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(originalProviderRequestId, resumedProviderRequestId);
Assert.Equal(1, downloadedAssetCountForRequest);
Assert.False(privateIpRedirectAccepted);
```

- [ ] เขียน `InProgressPollReschedules`, `CompletedResultDownloadedOnce`, `RedirectToPrivateIpRejected`, `SignedUrlRefreshUsesExistingRequest`, `CancelledDoesNotImplyRefund`, `UnknownCannotAutoFallback`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter ProviderRecoveryTests`; simulate 429, 5xx, delayed result และ expired media URL
- [ ] Implement GET retries, Retry-After, jitter, size/time limits และ host/IP validation ตาม D6. Auth header ให้ queue host เท่านั้น
- [ ] ทำ owner reconciliation command ที่แนบ existing provider request ID หรือยืนยัน no-charge/abandon พร้อม audit; ห้ามสร้าง ID ปลอมเพื่อข้าม UNKNOWN
- [ ] Restart หลัง output ถูกดาวน์โหลดแต่ก่อน DB finalize: dedupe ด้วย request/asset hash และไม่สร้าง billable job ใหม่
- [ ] Commit `feat: recover cloud generations and safely ingest completed media`

**Gate:** restart/sleep ไม่ทำให้สูญเสีย request reference; poll failure ไม่กลายเป็น paid submit

## T15 — End-to-end pipeline และ mock/live separation

**Depends:** T08,T10–T14

**Files:** Create `src/ContentFactory.Infrastructure/Providers/Mock/MockScriptPlanner.cs`, `src/ContentFactory.Infrastructure/Providers/Mock/MockVideoProvider.cs`, `tests/ContentFactory.IntegrationTests/EndToEndPipelineTests.cs`, `scripts/smoke-mock.sh`, `media/fixtures/README.md`

**Interfaces:** ทุก mock ใช้ interfaces C4 เดียวกับ live; fixture provenance metadata required; `make smoke-mock` เป็น public command

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(0, externalNetworkRequestCount);
Assert.Equal("h264", probedVideoCodec);
Assert.Equal("awaiting_review", finalVideoStatus);
```

- [ ] เขียน `TemplateOnlyProducesReviewableMp4`, `HybridMockUsesMotionAsset`, `MockCannotOpenExternalNetwork`, `PaidFlagOffBlocksLiveAdapterEvenWithKeyPresent`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter EndToEndPipelineTests`; mock result ต้องเป็น MP4 ที่ FFprobe อ่านได้จริง
- [ ] Wire orchestration/DI/config; ใส่ banner `MOCK MODE` ใน UI และ metadata ไม่สลับเป็น live เพราะ environment มี key เผลออยู่
- [ ] Script smoke สร้าง fixture -> campaign -> run -> poll result -> QA โดยไม่ owner approval อัตโนมัติ; review/export ทดสอบแยกภายหลัง
- [ ] รัน smoke 10 รอบเพื่อเช็ก pending/temp leak และ billable request count = 0
- [ ] Commit `feat: deliver a complete offline-safe media production pipeline`

**M2 Gate:** cloud path ทดสอบครบด้วย fake server; paid production ยังต้องผ่าน owner approval และ T22 live gate

---

## T16 — Owner review UI และ approval ที่ผูก exact version

**Depends:** T02,T12,T15

**Files:** Create `apps/dashboard/src/app/videos/[id]/page.tsx`, `apps/dashboard/src/components/VideoReview.tsx`, `src/ContentFactory.Core/Review/ReviewService.cs`, `src/ContentFactory.Api/Endpoints/ReviewEndpoints.cs`, `tests/ContentFactory.IntegrationTests/ApprovalTests.cs`, `apps/dashboard/e2e/review.spec.ts`

**Interfaces:** Implements IReviewService; consumes ReviewDecision/RenderResult/ValidationReport; POST /videos/{id}/reviews

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.False(exportAllowedForEditedManifest);
Assert.Equal(authenticatedOwnerId, savedApproval.ReviewerId);
Assert.Equal(reviewedManifestHash, savedApproval.ManifestSha256);
```

- [ ] เขียน `WrongManifestHashRejected`, `FailedQaCannotApprove`, `IncompleteChecklistRejected`, `EditedCaptionInvalidatesApproval`, `ReviewerIdentityComesFromSession`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter ApprovalTests` และ Playwright review spec; ยืนยัน RED
- [ ] ทำ preview MP4 + original product assets + approved claims + narration/caption + AI motion warning + cost breakdown; label ไม่ใช้สีเพียงอย่างเดียว
- [ ] Implement approval transaction และ optimistic concurrency; rerender creates new version และ owner ต้องดูใหม่
- [ ] รัน E2E กด approve, แก้หนึ่ง caption, verify export disabled สำหรับ version ใหม่; keyboard ใช้ review controls ได้
- [ ] Commit `feat: add accountable review with immutable approval bindings`

**Gate:** ไม่มี `approveAll=true` shortcut หรือสถานะ approved จาก mock planner

## T17 — Export package และการบันทึกเผยแพร่จริง

**Depends:** T03,T16

**Files:** Create `src/ContentFactory.Infrastructure/Media/ExportService.cs`, `src/ContentFactory.Api/Endpoints/ExportEndpoints.cs`, `src/ContentFactory.Api/Endpoints/PublicationEndpoints.cs`, `tests/ContentFactory.IntegrationTests/ExportTests.cs`, `apps/dashboard/e2e/export.spec.ts`

**Interfaces:** Implements IExportService; returns Archive AssetRef; consumes approved version/channel; produces package E3

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Contains("video.mp4", zipEntryNames);
Assert.Contains("sha256sums.txt", zipEntryNames);
Assert.Equal(0, publicationsCreatedByExport);
```

- [ ] เขียน `UnapprovedExportRejected`, `ApprovalForDifferentHashRejected`, `PackageContainsRequiredFilesAndChecksums`, `ExportDoesNotMarkPublished`, `StaleOfferRequiresVerification`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter ExportTests`; verify RED
- [ ] Implement immutable ZIP paths, manifest/checksums, current rights/offer check; no external platform API calls
- [ ] UI แสดง owner checklist การโพสต์/AI label/product link และช่องบันทึก post URL หลังทำจริง; link status unknown จน attest
- [ ] รัน E2E download ZIP เปิดดู MP4 และ verify sha256sums; replay export ไม่สร้าง duplicate package
- [ ] Commit `feat: export approved videos without pretending to auto-publish`

**Gate:** ผู้ใช้เอาไฟล์ไปเผยแพร่ได้จริง; ไม่มีการอ้างการปักตะกร้าที่ระบบไม่ได้ทำ

## T18 — Scheduling ที่รับมือ Mac sleep และ timezone

**Depends:** T07,T09,T17

**Files:** Create `src/ContentFactory.Infrastructure/Jobs/SchedulePlanner.cs`, `src/ContentFactory.Worker/Handlers/ScheduleTickHandler.cs`, `src/ContentFactory.Api/Endpoints/ScheduleEndpoints.cs`, `tests/ContentFactory.UnitTests/ScheduleTimezoneTests.cs`, `tests/ContentFactory.IntegrationTests/ScheduleRecoveryTests.cs`

**Interfaces:** Produces unique schedule occurrence jobs; settings include timezone/local_time/days/max jobs/enabled

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(1, occurrencesForRepeatedDstSlot.Count);
Assert.Equal(0, occurrencesForMissingDstSlot.Count);
Assert.InRange(jobsEnqueuedAfterWake, 0, 1);
```

- [ ] เขียน `BangkokScheduleUsesUtcInstant`, `DstMissingTimeSkippedWithWarning`, `DstRepeatedTimeRunsOnce`, `SleepMissedSlotsDoNotBurst`, `DailyJobLimitStillAppliesAfterRestart`
- [ ] รัน tests โดย FakeTimeProvider/known IANA timezone data; ไม่รอเวลาจริง
- [ ] Implement explicit policy: missed slots เกิน 60 นาที -> skip + notification; slot ใน 60 นาที -> enqueue ได้หนึ่งครั้ง; ไม่มี catch-up ทั้งวัน
- [ ] DST missing hour skip, repeated hour ใช้ occurrence แรก; slot key อ้าง local date/time/zone ไม่ทำ duplicate หลัง clock rollback
- [ ] รัน restart simulation; หยุด schedule ไม่ยกเลิก reservation cloud เดิม และไม่ auto-publish
- [ ] Commit `feat: schedule bounded production safely across restarts and timezones`

**Gate:** schedule default disabled; เปิด schedule แล้ว API/thermal/budget guards ยังมีผลครบ

## T19 — CSV analytics import และรายงานที่มี provenance

**Depends:** T17

**Files:** Create `src/ContentFactory.Infrastructure/Analytics/MetricsImporter.cs`, `src/ContentFactory.Infrastructure/Analytics/MetricsCalculator.cs`, `src/ContentFactory.Api/Endpoints/AnalyticsEndpoints.cs`, `apps/dashboard/src/app/analytics/page.tsx`, `tests/ContentFactory.UnitTests/MetricsTests.cs`, `tests/ContentFactory.IntegrationTests/MetricsImportTests.cs`

**Interfaces:** Produces metric_snapshots และ GET analytics/overview; accepts explicit column mapping/source/currency/as_of

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Null(ctrWhenClicksMissing);
Assert.Null(cvrWhenClicksAreZero);
Assert.Equal(rowCountAfterFirstImport, rowCountAfterSameImportAgain);
```

- [ ] เขียน `DuplicateImportDoesNotDoubleCount`, `CumulativeSnapshotsAreNotSummed`, `MissingClicksRemainNull`, `ZeroDenominatorReturnsNull`, `CurrenciesRemainSeparate`, `ReturnsReduceSettledCommission`
- [ ] รัน `dotnet test tests/ContentFactory.UnitTests --filter MetricsTests` และ MetricsImportTests; verify RED
- [ ] Implement streaming CSV parser, row validation/report, import idempotency; ไม่ประมวลผล formula หรือ macros. ไม่เก็บ buyer PII ที่ไม่จำเป็น
- [ ] UI แสดง source/date/currency/missing-data และ metric definitions; ไม่มี random sample values ใน production dashboard
- [ ] รัน import fixtures 2 ครั้ง และแก้ row version; ต้องเห็น reconciliation ที่ตรวจได้ ไม่เพิ่มยอดซ้ำ
- [ ] Commit `feat: import traceable performance metrics without fabricated attribution`

**M3 Gate:** owner ใช้ review/export/schedule/analytics ได้ และชัดเจนว่าส่วนไหนไม่มีข้อมูล/ยังไม่เผยแพร่

---

## T20 — Storage cleanup, backup และ restore safety barrier

**Depends:** T06,T14,T17

**Files:** Create `src/ContentFactory.Infrastructure/Storage/RetentionService.cs`, `src/ContentFactory.Worker/Health/DiskGuard.cs`, `src/ContentFactory.Cli/Commands/BackupCommand.cs`, `src/ContentFactory.Cli/Commands/RestoreCommand.cs`, `scripts/backup.sh`, `scripts/restore.sh`, `tests/ContentFactory.IntegrationTests/BackupRestoreTests.cs`

**Interfaces:** Produces backup manifest/schema version/file checksums; restore requires new target database + new DATA_ROOT + operator confirmation

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.True(allRestoredAssetHashesMatch);
Assert.False(paidLanesEnabledImmediatelyAfterRestore);
Assert.True(referencedAssetStillExistsAfterCleanup);
```

- [ ] เขียน `DiskPressureBlocksNewRender`, `ReferencedAssetNeverDeleted`, `ActiveAttemptTempNeverDeleted`, `RestoreVerifiesAllHashes`, `RestoreStartsWithPaidLanesPaused`
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter BackupRestoreTests`; backup tests ใช้ isolated fixture DB/files เท่านั้น
- [ ] Implement write-quiesce: pause new jobs + reject new mutations + drain local writers ก่อน dump/copy; cloud requests ยังอยู่ข้างนอก ต้องเก็บ IDs และไม่สัญญาว่าการ backup หยุดค่าใช้จ่าย cloud
- [ ] Backup DB ด้วย pg_dump compatible version พร้อม asset manifests/checksums; ไม่รวม env secrets; ตั้ง owner-only permissions และเอกสาร encryption/off-device copy
- [ ] Restore test ลง DB/path ใหม่; ตรวจไฟล์/count/state. Paid lanes ทั้งหมด paused จน reconcile ledger กับ provider history โดยเฉพาะ calls หลัง backup timestamp
- [ ] Commit `feat: protect local data with verified backups and safe restores`

**Gate:** ห้ามคืน backup แล้ว replay billable queued jobs อัตโนมัติ เพราะ backup อาจเก่ากว่า request ที่เคยถูกส่งจริง

## T21 — Operator dashboard, cost center และ diagnostics

**Depends:** T16–T20

**Files:** Create `apps/dashboard/src/app/page.tsx`, `apps/dashboard/src/app/jobs/page.tsx`, `apps/dashboard/src/app/costs/page.tsx`, `apps/dashboard/src/app/settings/page.tsx`, `src/ContentFactory.Api/Endpoints/SystemEndpoints.cs`, `apps/dashboard/e2e/operations.spec.ts`

**Interfaces:** Consumes API C5; displays persisted statuses only. Produces pause/resume controls และ readable readiness results

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```typescript
await expect(page.getByTestId("mode-banner")).toContainText("MOCK");
await expect(page.getByTestId("unknown-job-retry")).toHaveCount(0);
await expect(page.getByTestId("reserved-cost")).toBeVisible();
```

- [ ] เขียน E2E `EmptyDashboardShowsNoData`, `ReservedAndSpentAreDistinct`, `UnknownJobHasNoBlindRetryButton`, `PausedStatePersistsAfterRefresh`, `KeysAreNeverInPagePayload`
- [ ] รัน `npm --prefix apps/dashboard run test:e2e -- operations.spec.ts`; verify RED
- [ ] Implement job timeline, error explanations, remaining budget, stale pricing indicator, mode banner, manual review queue; ไม่มี mock charts ใน live mode
- [ ] Diagnostics แสดง LM connection, selected model ID, FFmpeg encoder/filter readiness, disk free, worker heartbeat. หาก thermal data unavailable แสดงตามจริง
- [ ] รัน UI tests desktop/แคบ 390px, keyboard focus, status text ไม่พึ่งสี; telemetry log เก็บ trace/job/attempt/provider IDs แต่ไม่ prompt/photo/keys
- [ ] Commit `feat: expose operational state and costs without misleading metrics`

**Gate:** เจ้าของตอบได้ว่างานไหนทำอะไรอยู่ ทำไมติด และมีเงินจอง/ใช้ไปเท่าไร

## T22 — Failure injection, local benchmark และ optional paid pilot

**Depends:** T00–T21

**Files:** Create `tests/ContentFactory.IntegrationTests/FailureInjectionTests.cs`, `scripts/benchmark-local.sh`, `scripts/live-preflight.sh`, `scripts/live-pilot.sh`, `docs/pilot-results.md`

**Interfaces:** Produces objective evidence; benchmark/live reports ต้องมี config/version/date และจำนวน samples

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(1, paidPostsInLostResponseScenario);
Assert.True(allUnknownRequestsKeepReservations);
Assert.Equal(0, automaticPaidReplaysAfterRestore);
```

- [ ] เขียน tests: kill before submit, kill after provider acceptance, lost response, lease expiry, duplicate callback/status, 429, invalid media, disk full, stale approval, old backup restore, offline/wake และ expired price
- [ ] รัน `dotnet test tests/ContentFactory.IntegrationTests --filter FailureInjectionTests`; assert billable POST count ไม่เพิ่มใน ambiguous cases และทุก failure มี durable state
- [ ] Benchmark local 10 template-only clips ด้วย fixed fixtures และ uploaded voice; วัด p50/p95 elapsed, memory pressure ที่อ่านได้, swap change, CPU, disk bytes และผล encoder probe. ไม่วัดเป็นอุณหภูมิถ้าไม่มี sensor
- [ ] Run live-preflight แบบ read-only: exact model/schema/price/caps/input rights/approval expiry; ไม่มี key หรือยังไม่อนุมัติ ให้รายงาน blocked ไม่แก้ flag เอง
- [ ] เฉพาะเมื่อเจ้าของยืนยัน live envelope: ทดสอบสูงสุด 10 requests fal, เลือก 5 ภาพสินค้า × 2 prompts, ไม่ paid rerun อัตโนมัติ; user ให้ pass/fail เรื่องรูปทรง/โลโก้/ความสอดคล้อง แล้วคำนวณ cost per accepted clip
- [ ] Commit `test: verify recovery invariants and document measured pilot results` โดยไม่ commit สื่อจริงของผู้ใช้

**Gate:** 10 samples เป็น exploratory pilot ไม่ใช่หลักฐานโมเดล “ดีที่สุด”. ค่า API/คุณภาพ/ความร้อนจริงต้องรายงาน ไม่แต่ง benchmark จากสเปก Mac

## T23 — CI, packaging, operator runbook และ release handoff

**Depends:** T22 (paid pilot อาจ blocked ได้ แต่ต้องระบุ release เป็น mock/local-only)

**Files:** Create `azure-pipelines.yml`, `scripts/verify-all.sh`, `scripts/verify-no-secrets.sh`, `scripts/dev-start.sh`, `scripts/dev-stop.sh`, `docs/operator-runbook.md`; Modify `README.md`, `docs/progress.md`, `docs/decisions.md`

**Interfaces:** Produces `make verify`, `make dev`, `make backup`, `make restore-test` และ release checklist ที่เจ้าของทำตามได้

**ตัวอย่าง assertions ที่ต้องพิสูจน์:** ตัวแปรด้านล่างเป็นผลจาก Arrange/Act ของ test fixture ที่ implementer สร้าง ไม่ใช่ public API เพิ่มเติม; ต้องใช้ชื่อ test และกรณีอื่นตาม checklist ด้วย

```csharp
Assert.Equal(0, paidNetworkCallsDuringVerification);
Assert.True(databaseVolumeExistsAfterDevStop);
Assert.Equal(0, detectedSecretCount);
```

- [ ] เขียน CI/config tests `BuildNeverRequiresPaidKeys`, `ProductionDefaultsRemainSafe`, `FreshCloneCanRunMockFlow`, `StopDoesNotDeleteDatabase`
- [ ] รัน verification ที่ยัง fail จาก scripts/pipeline ที่ไม่ครบ; ระบุ environment requirements ชัดเจน
- [ ] Implement lint/build/unit/integration/software-media/E2E mock + secret scan. ใช้ GitHub repository และ Azure DevOps pipeline เป็นตัวเลือกเริ่มต้นตามแนวทางที่ผู้ใช้คุ้นเคย; ไม่เชื่อม corporate account หรือ deploy ภายนอกอัตโนมัติ
- [ ] Test media บน Linux CI ใช้ software encoder และไม่ถือเป็นหลักฐาน VideoToolbox บน Mac; hardware gate มีผลจาก T22 local เท่านั้น
- [ ] ทำ fresh-clone walkthrough, owner init, mock flow, live enable/disable, backup/restore, cost troubleshooting และ sleep/wake recovery; ไม่ลง launchd auto-start หรือเปลี่ยน power settings โดยไม่อนุมัติ
- [ ] Commit `docs: publish verified local-first MVP and operating procedures`; tag `v0.1.0` เมื่อ owner รับรอง ไม่ push/tag remotely เอง

**M4 Gate:** ส่ง source + actual test evidence + known limitations + runbook ไม่ใช่แค่ภาพ dashboard

---

# ส่วน H — Acceptance และคู่มือปฏิบัติการที่ต้องส่งมอบ

## H1. Definition of Done ระดับระบบ

| Requirement | หลักฐานยอมรับ | Task |
|---|---|---|
| รันได้บน macOS ARM64 ของผู้ใช้ | doctor report + native media probe | T00,T11,T22 |
| ไม่บังคับ paid key เพื่อพัฒนา | fresh clone mock E2E ผ่าน | T15,T23 |
| มีสินค้า/claim/media provenance | integration tests + review UI | T03,T05,T16 |
| สร้าง MP4 จริงพร้อมเสียง/caption | FFprobe + sampled-frame/audio review | T10–T12 |
| ปลอดภัยต่อค่า API | budget race + uncertain submit tests | T09,T13,T14 |
| งานไม่หายหลัง restart | failure injection report | T06,T07,T22 |
| approval ตรง version ที่ส่งออก | stale hash test + package checksum | T16,T17 |
| automation schedule ไม่ยิงรวดหลัง wake | schedule recovery tests | T18 |
| ไม่มี double-count analytics | cumulative/import tests | T19 |
| backup ใช้คืนได้จริง | isolated restore + hash verification | T20 |
| credentials ไม่รั่ว | log/UI/config/secret-scan tests | T02,T21,T23 |
| ภาระเครื่อง/ต้นทุนทราบจากผลจริง | benchmark/pilot report | T22 |

Hard gates: ไม่พบ duplicate paid submit จาก blind retry ใน fault tests, ไม่พบ unknown claim หลุด validation, ไม่มี approval stale ถูกใช้ export, no paid traffic ใน CI/mock tests. นี่คือเกณฑ์ทดสอบ ไม่ใช่การรับประกันว่าจะไม่มีความผิดพลาดในอนาคต

## H2. การทดสอบความจุและความร้อน

เริ่ม `LLM=1`, `render=1`, `cloud_inflight=2`, quiet mode on. ทดสอบ pipeline จริงหนึ่งคลิปก่อน batch 10 คลิป. ไม่เพิ่ม concurrency ถ้า memory pressure เปลี่ยนเป็น warning, swap เพิ่มต่อเนื่อง หรือเครื่องตอบสนองชัดเจนว่าแย่ลง

แสดงตัวเลขที่วัดได้: stage elapsed, accepted/rejected clips, disk usage, RSS ของ processes, queue depth, restart recovery time. ไม่ทำ sensor polling ที่ต้อง sudo หรือปิดระบบ thermal management

การแปลง throughput เป็นกำลังผลิตต่อวันต้องระบุเวลาที่เปิดเครื่องจริง, API quota/wait, review time, acceptance rate และเวลาการใช้เครื่องทำงานอื่น. ไม่เอาเวลา render อย่างเดียวไปประกาศ 100 คลิป/วัน

## H3. Retention เริ่มต้น

| ข้อมูล | Policy ที่เสนอ |
|---|---|
| temp ของ attempt ที่ inactive | ลบหลัง 48 ชั่วโมงและไม่มี lease/reference |
| debug frame samples | 7 วัน ยกเว้นปักหมุดเพื่อแก้ปัญหา |
| structured operational logs | 14 วัน; ไม่มี raw secrets/prompt images |
| source assets/manifests/approvals/ledger | ไม่ลบอัตโนมัติใน MVP; owner archive/export ก่อน |
| export packages | เก็บจน owner ลบ; warning เมื่อ disk pressure |
| daily backups | 7 ชุด daily + 4 ชุด weekly ตามพื้นที่ที่จัดสรร |

Cleanup ต้องใช้ reference-aware queries; ห้าม `rm -rf DATA_ROOT/temp/*` ขณะมีงานทำอยู่. ลบ assets ต้องผ่าน UI/CLI explicit และบันทึก audit; ใช้ quarantine ก่อนลบถาวรได้

## H4. Startup / shutdown / recovery runbook

**เริ่ม:** เปิด Docker/PostgreSQL -> ตรวจ schema -> เปิด API/worker/UI -> เปิด LM Studio ที่เลือกเมื่อใช้ local AI -> doctor/readiness -> ทำ mock smoke -> เปิด production schedule ที่อนุมัติแล้ว

**หยุด:** pause new jobs -> รอ local writers/FFmpeg จบหรือ cancel อย่างปลอดภัย -> persist state -> stop app -> stop DB เมื่อไม่มี writers. Cloud jobs ที่ส่งแล้วอาจยังทำงานและคิดค่าบริการต่อ

**หลัง Mac wake/restart:** ตรวจ clock/network/disk -> reconnect DB -> watchdog ตรวจ leases -> reconcile provider requests เดิม -> resume local stage. ไม่ replay schedule backlog และไม่ auto-generate งานที่อยู่ `SUBMISSION_UNKNOWN`

**หลัง restore:** ไม่ resume paid lanes จนเทียบ provider history ตั้งแต่ backup timestamp กับ ledger. งานที่อาจจ่ายไปแล้วแต่ backup ไม่รู้ต้อง reconcile ก่อน; รักษาเอกสารแยกว่าข้อมูลหลัง snapshot อาจสูญหายตาม RPO

**เปิดข้ามคืน:** เป็นการตั้งค่าที่เจ้าของเลือกเอง; runbook อธิบาย sleep behavior และการเสียบไฟ แต่ไม่รัน `caffeinate` ถาวรหรือเปลี่ยน `pmset` โดยไม่บอกผู้ใช้

## H5. Cost worksheet

สำหรับ Wan720p ราคา snapshot USD0.10/attempt:

```text
planned attempts = finished videos x proportion using AI x AI shots per video
expected attempts = planned attempts x assumed retry/revision factor
estimated video API cost = expected attempts x price per attempt
cost per accepted AI shot = actual generation charges / accepted AI shots
```

ตัวอย่างสมมติ: 300 final clips/month × 40% ที่ใช้ AI × 1 shot × 1.25 attempts = 150 attempts; 150 × USD0.10 = USD15 เฉพาะ video API. อัตรา reject/revision 25% เป็นสมมติฐาน ไม่ใช่ผลทดสอบ. เก็บจำนวน paid attempts จริงแทนการคูณอัตราประมาณหลังผลิต

สูตร budget guard ใช้ reservation ไม่ใช่ expected cost; หาก accepted=0 ให้แสดง undefined/not available แทนหารศูนย์. ค่า Go subscription สำหรับการเขียนระบบเป็นอีกหมวด ไม่ใช่เครดิตแลก video หรือเงินใน fal wallet

## H6. ข้อเสนอขยายหลัง MVP เท่านั้น

เมื่อมีหลักฐาน bottleneck จึงพิจารณา n8n notifications, object storage staging, paid text fallback, alternative video provider, approved scheduler, multi-language reviewers และ cloud workers. แต่ละรายการต้องมี ADR/contract/tests ของตัวเอง

Cloud migration ต้องเปลี่ยน local filesystem เป็น storage adapter และแยก platform-specific encoder profile; ไม่สมมติว่า ffmpeg VideoToolbox ใช้ได้บน Linux. ไม่จำเป็นต้องย้าย job engine ไป Temporal/RabbitMQ จนเห็นเหตุผลที่ PostgreSQL queue ไม่พอ

---

# ส่วน I — วิธีส่งงานให้ DeepSeek ผ่าน OpenCode

## I1. เตรียมไฟล์

วาง `AGENTS.md` ที่ root ของ repository; วางเอกสารแผนนี้และ `START_HERE_DEEPSEEK.md` ใน `docs/`. หาก repo มี AGENTS.md อยู่แล้วให้ merge rules โดยไม่ลบคำสั่งเดิมที่ผู้ใช้ตั้งไว้

OpenCode รองรับ project rules ผ่าน AGENTS.md [S15]. การอ้าง path ใน AGENTS ไม่ได้แปลว่าโหลดไฟล์นั้นอัตโนมัติทุกกรณี จึงต้องสั่ง agent ให้อ่านช่วง spec/contracts/task ที่เกี่ยวข้องก่อนลงมือ และใช้ progress file เป็นสถานะข้าม session

เชื่อม OpenCode Go ด้วย `/connect` แล้วเลือก model ผ่าน `/models`. Model identifier ที่เอกสาร Go แสดงสำหรับกรณีนี้ [S01]:

```json
{
  "$schema": "https://opencode.ai/config.json",
  "model": "opencode-go/deepseek-v4.1-flash"
}
```

ตรวจใน `/models` อีกครั้งเมื่อเริ่ม เพราะ availability/ชื่อ routing เปลี่ยนได้. ห้ามเอา model ID ของ direct DeepSeek API มาแทน provider-qualified ID โดยไม่ตรวจ provider configuration

## I2. Prompt เริ่มต้นที่ให้ agent ลงมือจริง

```text
อ่าน AGENTS.md, docs/START_HERE_DEEPSEEK.md และ
ส่วน A–F ของ docs/AI_CONTENT_FACTORY_DEVELOPMENT_PLAN.md ก่อน

นี่คือโปรเจกต์ใหม่สำหรับ Mac ของฉัน ให้พัฒนาตามแผน ไม่ใช่สรุปแผนกลับมาอย่างเดียว
เริ่มเฉพาะ M0 / T00–T05 และทำทีละ task แบบ RED -> GREEN -> REVIEW
ตรวจ repository ก่อนสร้างไฟล์ และอย่าทับไฟล์เดิมที่ไม่เกี่ยวข้อง

ใช้ OpenCode Go เป็น coding agent เท่านั้น
runtime เริ่ม mock providers; ALLOW_PAID_APIS=false; AUTO_PUBLISH=false
ห้ามเรียก paid API, ขอซื้อเครดิต, เปิด public endpoint, ปิด security หรือเพิ่ม dependency ใหญ่เอง

เมื่อจบแต่ละ task ให้รัน tests ที่ระบุ บันทึก commands/ผลจริงใน docs/progress.md
แล้ว commit เฉพาะไฟล์เกี่ยวข้องเมื่อ git พร้อม
หากไม่มี prerequisite ให้รายงานข้อจำกัดและทำส่วนที่ไม่ติดขัดต่อได้
อย่าใส่ success ปลอม อย่าข้าม tests และอย่าแก้ architecture โดยไม่บันทึกเหตุผล

หยุดเมื่อจบ M0 พร้อมสรุปไฟล์ที่สร้าง ผล tests สิ่งที่ยัง blocked และ task ถัดไป
```

## I3. Prompt สำหรับ session ถัดไป

```text
อ่าน AGENTS.md และ docs/progress.md ก่อน
เทียบ git status กับ progress เพื่อไม่ทำงานเดิมซ้ำ
อ่าน spec/contracts และ task ถัดไปที่ยังไม่เสร็จในแผน
ทำเฉพาะ milestone ถัดไปที่ไม่มี blocker โดยคง safe defaults ทั้งหมด
ก่อนแก้ shared contract ให้อธิบายผลต่อ downstream และอัปเดต tests/callers ให้ตรงกัน
สรุปผลจากสิ่งที่รันจริง ไม่ใช่จากการคาดเดา
```

## I4. progress.md template ที่ agent ต้องสร้าง

```markdown
# Development progress

## Current milestone
M0

## Task status
| Task | Status | Commit | Evidence | Blocker |
|---|---|---|---|---|
| T00 | not_started | - | - | - |

## Last verified commands
- Command:
- Exit code:
- Tests passed/failed/skipped:
- Environment and timestamp:

## Architecture decisions changed
- None

## Paid API activity
- Disabled; no paid requests authorized

## Next exact task
- Task ID:
- Inputs/files to read:
- First failing test:
```

Status ใช้ `not_started`, `in_progress`, `blocked`, `done`. `done` ต้องมี evidence; `skipped` ไม่ถือว่า pass. ไม่ให้ agent สรุปงานทั้งหมดว่าเสร็จเพราะ UI เปิดได้

## I5. ขอบเขตอำนาจ coding agent

Agent มีสิทธิ์เขียนไฟล์โปรเจกต์ รันทดสอบที่ไม่มีค่า API และเสนอ commit. ไม่มีสิทธิ์อนุมัติ live envelope แทนเจ้าของ, เปิด subscription overage, เปลี่ยนแพ็กเกจ Go, ใช้ corporate credentials, เผยแพร่โพสต์ หรือแก้ firewall/power management โดยไม่รับคำสั่งเฉพาะ

Quota ของ coding agent หมดให้หยุด checkpoint ไม่เปลี่ยนโมเดลแพงหรือใช้ยอดเงินสำรองอัตโนมัติ. ตั้งค่าการใช้ balance/overage ของบัญชีต้องตรวจใน Console โดยเจ้าของ; ไม่ให้โปรเจกต์นี้แก้การตั้งค่าบัญชี

## I6. ประเด็น implementation ที่ต้องตัดสินให้ตรงกัน

- `POST /auth/login` ยกเว้นข้อกำหนด existing authenticated session แต่ยังตรวจ Origin/CSRF/login rate limit และ credentials; `GET /auth/csrf` ออก token สำหรับหน้า login ได้. Mutations อื่นทั้งหมดต้อง authenticated
- Review state ใช้ชุด explicit: `draft`, `building`, `qa_failed`, `awaiting_review`, `approved`, `rejected`, `blocked`, `cancelled`. Export/publication เก็บแยก ไม่เขียน state published ใน video เพื่อแทนหลักฐานการโพสต์
- Cost ledger ใช้ idempotency key ต่อ event เพื่อยอมให้มี adjustment/refund หลายเหตุการณ์; settlement ซ้ำด้วย event key เดิมไม่เพิ่มยอด
- Worker handler เสร็จต้องยืนยัน fence ใน DB transaction. Asset ที่เขียนแล้วแต่ transaction ไม่สำเร็จเป็น orphan ที่ cleanup ภายหลัง ไม่ใช่ผลสำเร็จของงาน
- New Next.js components ใช้ TypeScript strict และ generated API types จาก OpenAPI; ห้าม duplicate DTO ด้วยชื่อ field ต่างกัน. ห้าม import server secret/config เข้า client components
- Scope การทดสอบ integration ตั้งจาก explicit test connection string; validate database name prefix `cf_test_` ก่อน create/drop และห้าม fallback ไป app connection string

---

# ส่วน J — แหล่งอ้างอิงและ preflight checklist

## J1. เอกสารทางการ

ตรวจเมื่อ 20 ตุลาคม 2026. การตัดสินใจเรื่อง concurrency, architecture, budgets, timeouts และ task order ในไฟล์นี้เป็นข้อเสนอออกแบบ ไม่ใช่ข้อกำหนดที่ผู้ให้บริการรับประกัน

- [S01] OpenCode Go: รายการโมเดล, coding-agent intended use, API routing, usage limits และการเชื่อม provider
- [S02] DeepSeek V4.1 Flash official model card: modalities และลักษณะ text generation
- [S03] Microsoft .NET support policy: .NET 10 LTS และวันสิ้นสุด .NET 8
- [S04] Next.js support policy: stable supported major
- [S05] Node.js 24 LTS release/support
- [S06] LM Studio structured output API
- [S07] LM Studio API token authentication
- [S08] Apple VideoToolbox: hardware video encoding/decoding
- [S09] Docker Desktop virtual machine managers: Linux VM/container boundary
- [S10] fal Wan Turbo model page: ราคาต่อวิดีโอ
- [S11] fal Wan API: queue/files/data URI และ input/output
- [S12] fal Wan Turbo official OpenAPI schema: exact fields/enums/paths
- [S13] fal asynchronous queue inference: submit/status/result/cancel
- [S14] TikTok Content Sharing Guidelines: intended use และ publishing constraints
- [S15] OpenCode project rules: AGENTS.md และ instruction handling
- [S16] PostgreSQL SELECT: row locking และ SKIP LOCKED
- [S17] Azure Speech text-to-speech REST: voices/auth/synthesis
- [S18] FFmpeg official documentation: installed features/encoding/filter/probe

[S01]: https://opencode.ai/v2/docs/console/go/
[S02]: https://huggingface.co/deepseek-ai/DeepSeek-V4.1-Flash/blob/main/README.md
[S03]: https://dotnet.microsoft.com/en-us/platform/support/policy
[S04]: https://nextjs.org/support-policy
[S05]: https://nodejs.org/en/blog/release/v24.11.0
[S06]: https://lmstudio.ai/docs/developer/openai-compat/structured-output
[S07]: https://lmstudio.ai/docs/developer/core/authentication
[S08]: https://developer.apple.com/documentation/videotoolbox
[S09]: https://docs.docker.com/desktop/features/vmm/
[S10]: https://fal.ai/models/fal-ai/wan/v2.2-a14b/image-to-video/turbo
[S11]: https://fal.ai/models/fal-ai/wan/v2.2-a14b/image-to-video/turbo/api
[S12]: https://fal.ai/api/openapi/queue/openapi.json?endpoint_id=fal-ai/wan/v2.2-a14b/image-to-video/turbo
[S13]: https://fal.ai/docs/documentation/model-apis/inference/queue
[S14]: https://developers.tiktok.com/docs/en/content-sharing-guidelines
[S15]: https://opencode.ai/docs/rules/
[S16]: https://www.postgresql.org/docs/18/sql-select.html
[S17]: https://learn.microsoft.com/th-th/azure/ai-services/speech-service/rest-text-to-speech
[S18]: https://ffmpeg.org/documentation.html

## J2. สิ่งที่ต้องตรวจใหม่ก่อนเริ่ม live

- [ ] รุ่น/หน่วยความจำ/disk/macOS ของเครื่องจริง และสิทธิ์ติดตั้ง tooling บนอุปกรณ์นี้
- [ ] ถ้าเป็นเครื่ององค์กร: ได้รับอนุญาตทำโปรเจกต์/ใช้ Docker/ส่งข้อมูลสินค้าไป cloud; ไม่ใช้ข้อมูล HR หรืองานบริษัทเป็น fixture
- [ ] .NET/Node/Next/EF/Npgsql/FFmpeg versions ที่ pin ยังได้รับ security updates
- [ ] OpenCode `/models` แสดง DeepSeek ที่ต้องการ และเข้าใจ quota/overage ของแพ็กเกจจริง
- [ ] Runtime LLM ที่เลือกโหลดได้และผ่าน schema/ภาษาไทย; ไม่ใช้ Go เป็น production content API โดยอัตโนมัติ
- [ ] fal endpoint/schema/price/billing/cancellation/commercial terms ยังตรง snapshot
- [ ] Voice endpoint/voice locale/ราคา/สิทธิ์เสียง พร้อมถ้าเปิด Azure Speech; ไม่มีราคา speech ที่เดาเอง
- [ ] เจ้าของยืนยัน rights และ cloud consent ของรูปสินค้าทุกภาพ
- [ ] Account/market/affiliate/disclosure requirements ตรวจในแพลตฟอร์มจริง; publishing ยังคง manual export
- [ ] Live approval envelope ระบุจำนวนคำขอ งบ วันหมดอายุ และห้าม provider fallback
- [ ] mock E2E, failure injection และ isolated restore ผ่านแล้ว
- [ ] Owner พร้อมตรวจคุณภาพวิดีโอจริง; ถ้าไม่มี reviewer ไม่เปิด schedule ผลิตสินค้าเพื่อเผยแพร่จริง

## J3. ข้อสรุปการส่งมอบ

เอกสารนี้เพียงพอให้เริ่มพัฒนาโดยไม่ต้องเติม API endpoint หรือข้อจำกัดสำคัญจากการคาดเดา. รายละเอียดที่ต้องใช้บัญชีจริง/สิทธิ์จริง/benchmark จริงถูกกำหนดเป็น preflight และ live gates ไม่ใช่ success ที่สมมติขึ้น

เป้าหมายแรกคือ **สินค้า 1 รายการ -> MP4 จริง 1 คลิป -> owner review -> export ที่ตรวจ hash ได้**. เมื่อเส้นทางนี้ผ่านจึงเปิดการตั้งเวลาและเพิ่มจำนวนงาน ไม่สร้างระบบจำนวนมากก่อนพิสูจน์หนึ่งรอบให้ครบ
