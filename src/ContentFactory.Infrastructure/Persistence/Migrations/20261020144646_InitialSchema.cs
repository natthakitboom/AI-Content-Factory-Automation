using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ContentFactory.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "app");

            migrationBuilder.CreateTable(
                name: "assets",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    mime = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    bytes = table.Column<long>(type: "bigint", nullable: false),
                    width = table.Column<int>(type: "integer", nullable: true),
                    height = table.Column<int>(type: "integer", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    rights_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    source_reference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    cloud_use_allowed = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.id);
                    table.CheckConstraint("ck_assets_kind", "kind IN ('image','audio','video','subtitle','thumbnail','manifest','archive')");
                    table.CheckConstraint("ck_assets_sha256", "sha256 ~ '^[0-9a-f]{64}$'");
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    target_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    target_id = table.Column<Guid>(type: "uuid", nullable: true),
                    details_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "budget_periods",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    scope_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    scope_id = table.Column<Guid>(type: "uuid", nullable: false),
                    period_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    period_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    limit_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    reserved_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    spent_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_periods", x => x.id);
                    table.CheckConstraint("ck_budget_periods_amounts", "limit_usd >= 0 AND reserved_usd >= 0 AND spent_usd >= 0");
                    table.CheckConstraint("ck_budget_periods_scope", "scope_type IN ('monthly','daily','campaign','video')");
                });

            migrationBuilder.CreateTable(
                name: "channels",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    platform = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    market = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    locale = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    capabilities_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_channels", x => x.id);
                    table.CheckConstraint("ck_channels_status", "status IN ('pending_review','active','disabled')");
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    payload_version = table.Column<int>(type: "integer", nullable: false),
                    dedupe_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    lane = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    run_after = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    lease_owner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fence = table.Column<long>(type: "bigint", nullable: false),
                    last_error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_jobs", x => x.id);
                    table.CheckConstraint("ck_jobs_attempts", "attempts >= 0 AND max_attempts > 0");
                    table.CheckConstraint("ck_jobs_lane", "lane IN ('control','heavy_local','cloud_submit','cloud_poll','download')");
                    table.CheckConstraint("ck_jobs_status", "status IN ('queued','running','succeeded','retry_wait','paused','failed','cancelled','blocked')");
                });

            migrationBuilder.CreateTable(
                name: "outbox_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    aggregate_id = table.Column<Guid>(type: "uuid", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_events", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "owner_users",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    username = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    password_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    active = table.Column<bool>(type: "boolean", nullable: false),
                    session_version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_owner_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "price_snapshots",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    config_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unit = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    unit_price_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    source_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_price_snapshots", x => x.id);
                    table.CheckConstraint("ck_price_snapshots_price", "unit_price_usd >= 0");
                });

            migrationBuilder.CreateTable(
                name: "products",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    external_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    title = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    market = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    locale = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    category = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_products", x => x.id);
                    table.CheckConstraint("ck_products_locale", "locale <> ''");
                    table.CheckConstraint("ck_products_market", "market ~ '^[A-Z]{2}$'");
                });

            migrationBuilder.CreateTable(
                name: "job_attempts",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    fence = table.Column<long>(type: "bigint", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ended_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    error_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    trace_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_attempts_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "app",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "job_events",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    details_json = table.Column<string>(type: "jsonb", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_job_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_job_events_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "app",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_requests",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    client_operation_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    request_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    provider_request_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    response_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_requests", x => x.id);
                    table.CheckConstraint("ck_provider_requests_status", "status IN ('prepared','submitting','submitted','in_progress','succeeded','failed_confirmed','submission_unknown','cancellation_requested','cancelled_confirmed')");
                    table.ForeignKey(
                        name: "FK_provider_requests_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "app",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "cost_entries",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    operation_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    entry_type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    amount_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    certainty = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    provider_request_id = table.Column<Guid>(type: "uuid", nullable: true),
                    price_snapshot_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cost_entries", x => x.id);
                    table.CheckConstraint("ck_cost_entries_certainty", "certainty IN ('confirmed','estimated')");
                    table.CheckConstraint("ck_cost_entries_type", "entry_type IN ('reserve','settle','release','adjustment','refund')");
                    table.ForeignKey(
                        name: "FK_cost_entries_price_snapshots_price_snapshot_id",
                        column: x => x.price_snapshot_id,
                        principalSchema: "app",
                        principalTable: "price_snapshots",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "campaigns",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    mode = table.Column<string>(type: "text", nullable: false),
                    locale = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    target_duration_ms = table.Column<int>(type: "integer", nullable: false),
                    budget_usd = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_campaigns", x => x.id);
                    table.CheckConstraint("ck_campaigns_budget", "budget_usd >= 0");
                    table.CheckConstraint("ck_campaigns_duration", "target_duration_ms BETWEEN 15000 AND 35000");
                    table.CheckConstraint("ck_campaigns_mode", "mode IN ('template_only','hybrid_motion')");
                    table.CheckConstraint("ck_campaigns_status", "status IN ('draft','active','paused','archived')");
                    table.ForeignKey(
                        name: "FK_campaigns_channels_channel_id",
                        column: x => x.channel_id,
                        principalSchema: "app",
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_campaigns_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "app",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_assets",
                schema: "app",
                columns: table => new
                {
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_assets", x => new { x.product_id, x.asset_id });
                    table.ForeignKey(
                        name: "FK_product_assets_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "app",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_assets_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "app",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_claims",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    source_reference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    approved_by = table.Column<Guid>(type: "uuid", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_claims", x => x.id);
                    table.CheckConstraint("ck_product_claims_source", "source_reference <> ''");
                    table.CheckConstraint("ck_product_claims_status", "status IN ('pending','approved','rejected','expired')");
                    table.ForeignKey(
                        name: "FK_product_claims_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "app",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_offers",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    market = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    product_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    price = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    commission_rate = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    availability = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    checked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_offers", x => x.id);
                    table.CheckConstraint("ck_product_offers_commission_rate", "commission_rate >= 0 AND commission_rate <= 1");
                    table.CheckConstraint("ck_product_offers_currency", "currency ~ '^[A-Z]{3}$'");
                    table.CheckConstraint("ck_product_offers_price", "price >= 0");
                    table.ForeignKey(
                        name: "FK_product_offers_products_product_id",
                        column: x => x.product_id,
                        principalSchema: "app",
                        principalTable: "products",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "content_revisions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    revision = table.Column<int>(type: "integer", nullable: false),
                    product_snapshot_json = table.Column<string>(type: "jsonb", nullable: false),
                    script_json = table.Column<string>(type: "jsonb", nullable: true),
                    script_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    prompt_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    model_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_revisions", x => x.id);
                    table.CheckConstraint("ck_content_revisions_revision", "revision > 0");
                    table.CheckConstraint("ck_content_revisions_status", "status IN ('draft','validated','blocked','archived')");
                    table.ForeignKey(
                        name: "FK_content_revisions_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalSchema: "app",
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "schedules",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    campaign_id = table.Column<Guid>(type: "uuid", nullable: false),
                    timezone = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    days_of_week = table.Column<int[]>(type: "integer[]", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    max_jobs_per_day = table.Column<int>(type: "integer", nullable: false),
                    last_slot = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedules", x => x.id);
                    table.CheckConstraint("ck_schedules_max_jobs", "max_jobs_per_day > 0");
                    table.ForeignKey(
                        name: "FK_schedules_campaigns_campaign_id",
                        column: x => x.campaign_id,
                        principalSchema: "app",
                        principalTable: "campaigns",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "scene_assets",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    scene_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    role = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    asset_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_scene_assets", x => x.id);
                    table.ForeignKey(
                        name: "FK_scene_assets_assets_asset_id",
                        column: x => x.asset_id,
                        principalSchema: "app",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_scene_assets_content_revisions_content_revision_id",
                        column: x => x.content_revision_id,
                        principalSchema: "app",
                        principalTable: "content_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "video_versions",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_revision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    version_no = table.Column<int>(type: "integer", nullable: false),
                    manifest_json = table.Column<string>(type: "jsonb", nullable: true),
                    manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    output_asset_id = table.Column<Guid>(type: "uuid", nullable: true),
                    qa_json = table.Column<string>(type: "jsonb", nullable: true),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_video_versions", x => x.id);
                    table.CheckConstraint("ck_video_versions_status", "status IN ('draft','building','qa_failed','awaiting_review','approved','rejected','blocked','cancelled')");
                    table.ForeignKey(
                        name: "FK_video_versions_assets_output_asset_id",
                        column: x => x.output_asset_id,
                        principalSchema: "app",
                        principalTable: "assets",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_video_versions_content_revisions_content_revision_id",
                        column: x => x.content_revision_id,
                        principalSchema: "app",
                        principalTable: "content_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "schedule_occurrences",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    slot_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    job_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_occurrences", x => x.id);
                    table.ForeignKey(
                        name: "FK_schedule_occurrences_jobs_job_id",
                        column: x => x.job_id,
                        principalSchema: "app",
                        principalTable: "jobs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_schedule_occurrences_schedules_schedule_id",
                        column: x => x.schedule_id,
                        principalSchema: "app",
                        principalTable: "schedules",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "approvals",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    manifest_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reviewer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    decision = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    checklist_json = table.Column<string>(type: "jsonb", nullable: false),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_approvals", x => x.id);
                    table.CheckConstraint("ck_approvals_decision", "decision IN ('approved','rejected')");
                    table.ForeignKey(
                        name: "FK_approvals_video_versions_video_version_id",
                        column: x => x.video_version_id,
                        principalSchema: "app",
                        principalTable: "video_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "export_packages",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    package_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    relative_path = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_packages", x => x.id);
                    table.ForeignKey(
                        name: "FK_export_packages_channels_channel_id",
                        column: x => x.channel_id,
                        principalSchema: "app",
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_export_packages_video_versions_video_version_id",
                        column: x => x.video_version_id,
                        principalSchema: "app",
                        principalTable: "video_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "publications",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    video_version_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel_id = table.Column<Guid>(type: "uuid", nullable: false),
                    post_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    external_post_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    product_link_status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    evidence_reference = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publications", x => x.id);
                    table.CheckConstraint("ck_publications_product_link", "product_link_status IN ('unknown','verified','broken','not_applicable')");
                    table.ForeignKey(
                        name: "FK_publications_channels_channel_id",
                        column: x => x.channel_id,
                        principalSchema: "app",
                        principalTable: "channels",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publications_video_versions_video_version_id",
                        column: x => x.video_version_id,
                        principalSchema: "app",
                        principalTable: "video_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metric_snapshots",
                schema: "app",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    publication_id = table.Column<Guid>(type: "uuid", nullable: false),
                    source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    as_of = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    views = table.Column<long>(type: "bigint", nullable: true),
                    product_clicks = table.Column<long>(type: "bigint", nullable: true),
                    orders = table.Column<long>(type: "bigint", nullable: true),
                    commission_pending = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    commission_settled = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    import_key = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_metric_snapshots", x => x.id);
                    table.CheckConstraint("ck_metric_snapshots_counts", "(views IS NULL OR views >= 0) AND (product_clicks IS NULL OR product_clicks >= 0) AND (orders IS NULL OR orders >= 0)");
                    table.CheckConstraint("ck_metric_snapshots_currency", "currency = '' OR currency ~ '^[A-Z]{3}$'");
                    table.ForeignKey(
                        name: "FK_metric_snapshots_publications_publication_id",
                        column: x => x.publication_id,
                        principalSchema: "app",
                        principalTable: "publications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_approvals_video_manifest",
                schema: "app",
                table: "approvals",
                columns: new[] { "video_version_id", "manifest_sha256" });

            migrationBuilder.CreateIndex(
                name: "ux_assets_sha256_kind",
                schema: "app",
                table: "assets",
                columns: new[] { "sha256", "kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_created",
                schema: "app",
                table: "audit_events",
                column: "created_at");

            migrationBuilder.CreateIndex(
                name: "ix_audit_events_target",
                schema: "app",
                table: "audit_events",
                columns: new[] { "target_type", "target_id" });

            migrationBuilder.CreateIndex(
                name: "ux_budget_periods_scope_period",
                schema: "app",
                table: "budget_periods",
                columns: new[] { "scope_type", "scope_id", "period_start", "period_end" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_channel_id",
                schema: "app",
                table: "campaigns",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "IX_campaigns_product_id",
                schema: "app",
                table: "campaigns",
                column: "product_id");

            migrationBuilder.CreateIndex(
                name: "ux_content_revisions_campaign_revision",
                schema: "app",
                table: "content_revisions",
                columns: new[] { "campaign_id", "revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_cost_entries_operation",
                schema: "app",
                table: "cost_entries",
                column: "operation_id");

            migrationBuilder.CreateIndex(
                name: "IX_cost_entries_price_snapshot_id",
                schema: "app",
                table: "cost_entries",
                column: "price_snapshot_id");

            migrationBuilder.CreateIndex(
                name: "ux_cost_entries_idempotency_key",
                schema: "app",
                table: "cost_entries",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_export_packages_channel_id",
                schema: "app",
                table: "export_packages",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "ux_export_packages_video_channel_hash",
                schema: "app",
                table: "export_packages",
                columns: new[] { "video_version_id", "channel_id", "package_hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_job_attempts_job_attempt",
                schema: "app",
                table: "job_attempts",
                columns: new[] { "job_id", "attempt_no" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_job_events_job_occurred",
                schema: "app",
                table: "job_events",
                columns: new[] { "job_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_jobs_eligible",
                schema: "app",
                table: "jobs",
                columns: new[] { "lane", "priority", "run_after", "created_at" },
                filter: "status = 'queued'");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_status_lease",
                schema: "app",
                table: "jobs",
                columns: new[] { "status", "lease_until" });

            migrationBuilder.CreateIndex(
                name: "ux_jobs_dedupe_key",
                schema: "app",
                table: "jobs",
                column: "dedupe_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_metric_snapshots_publication_id",
                schema: "app",
                table: "metric_snapshots",
                column: "publication_id");

            migrationBuilder.CreateIndex(
                name: "ux_metric_snapshots_import_key",
                schema: "app",
                table: "metric_snapshots",
                column: "import_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_unpublished",
                schema: "app",
                table: "outbox_events",
                columns: new[] { "published_at", "created_at" },
                filter: "published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "ux_owner_users_username",
                schema: "app",
                table: "owner_users",
                column: "username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_price_snapshots_lookup",
                schema: "app",
                table: "price_snapshots",
                columns: new[] { "provider", "model", "config_hash", "valid_until" });

            migrationBuilder.CreateIndex(
                name: "IX_product_assets_asset_id",
                schema: "app",
                table: "product_assets",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ix_product_claims_product_status",
                schema: "app",
                table: "product_claims",
                columns: new[] { "product_id", "status" });

            migrationBuilder.CreateIndex(
                name: "ix_product_offers_product_market",
                schema: "app",
                table: "product_offers",
                columns: new[] { "product_id", "market" });

            migrationBuilder.CreateIndex(
                name: "ux_products_market_external_id",
                schema: "app",
                table: "products",
                columns: new[] { "market", "external_id" },
                unique: true,
                filter: "external_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_provider_requests_job_id",
                schema: "app",
                table: "provider_requests",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ux_provider_requests_client_operation",
                schema: "app",
                table: "provider_requests",
                column: "client_operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_publications_channel_id",
                schema: "app",
                table: "publications",
                column: "channel_id");

            migrationBuilder.CreateIndex(
                name: "IX_publications_video_version_id",
                schema: "app",
                table: "publications",
                column: "video_version_id");

            migrationBuilder.CreateIndex(
                name: "IX_scene_assets_asset_id",
                schema: "app",
                table: "scene_assets",
                column: "asset_id");

            migrationBuilder.CreateIndex(
                name: "ux_scene_assets_revision_scene_role",
                schema: "app",
                table: "scene_assets",
                columns: new[] { "content_revision_id", "scene_id", "role" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedule_occurrences_job_id",
                schema: "app",
                table: "schedule_occurrences",
                column: "job_id");

            migrationBuilder.CreateIndex(
                name: "ux_schedule_occurrences_schedule_slot",
                schema: "app",
                table: "schedule_occurrences",
                columns: new[] { "schedule_id", "slot_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_schedules_campaign_id",
                schema: "app",
                table: "schedules",
                column: "campaign_id");

            migrationBuilder.CreateIndex(
                name: "IX_video_versions_output_asset_id",
                schema: "app",
                table: "video_versions",
                column: "output_asset_id");

            migrationBuilder.CreateIndex(
                name: "ux_video_versions_revision_version",
                schema: "app",
                table: "video_versions",
                columns: new[] { "content_revision_id", "version_no" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "approvals",
                schema: "app");

            migrationBuilder.DropTable(
                name: "audit_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "budget_periods",
                schema: "app");

            migrationBuilder.DropTable(
                name: "cost_entries",
                schema: "app");

            migrationBuilder.DropTable(
                name: "export_packages",
                schema: "app");

            migrationBuilder.DropTable(
                name: "job_attempts",
                schema: "app");

            migrationBuilder.DropTable(
                name: "job_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "metric_snapshots",
                schema: "app");

            migrationBuilder.DropTable(
                name: "outbox_events",
                schema: "app");

            migrationBuilder.DropTable(
                name: "owner_users",
                schema: "app");

            migrationBuilder.DropTable(
                name: "product_assets",
                schema: "app");

            migrationBuilder.DropTable(
                name: "product_claims",
                schema: "app");

            migrationBuilder.DropTable(
                name: "product_offers",
                schema: "app");

            migrationBuilder.DropTable(
                name: "provider_requests",
                schema: "app");

            migrationBuilder.DropTable(
                name: "scene_assets",
                schema: "app");

            migrationBuilder.DropTable(
                name: "schedule_occurrences",
                schema: "app");

            migrationBuilder.DropTable(
                name: "price_snapshots",
                schema: "app");

            migrationBuilder.DropTable(
                name: "publications",
                schema: "app");

            migrationBuilder.DropTable(
                name: "jobs",
                schema: "app");

            migrationBuilder.DropTable(
                name: "schedules",
                schema: "app");

            migrationBuilder.DropTable(
                name: "video_versions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "assets",
                schema: "app");

            migrationBuilder.DropTable(
                name: "content_revisions",
                schema: "app");

            migrationBuilder.DropTable(
                name: "campaigns",
                schema: "app");

            migrationBuilder.DropTable(
                name: "channels",
                schema: "app");

            migrationBuilder.DropTable(
                name: "products",
                schema: "app");
        }
    }
}
