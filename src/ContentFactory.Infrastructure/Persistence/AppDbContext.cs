using System.Text.RegularExpressions;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace ContentFactory.Infrastructure.Persistence;

/// <summary>
/// PostgreSQL source of truth for products, immutable revisions, jobs, budgets and audit.
/// Table and column names are snake_case; JSON enums are stored as snake_case strings.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options, TimeProvider? timeProvider = null)
        : base(options)
    {
        Clock = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Injectable clock for entities created by application code.</summary>
    public TimeProvider Clock { get; }

    public DbSet<OwnerUser> OwnerUsers => Set<OwnerUser>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductClaim> ProductClaims => Set<ProductClaim>();
    public DbSet<ProductOffer> ProductOffers => Set<ProductOffer>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<ProductAsset> ProductAssets => Set<ProductAsset>();
    public DbSet<Channel> Channels => Set<Channel>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<ContentRevision> ContentRevisions => Set<ContentRevision>();
    public DbSet<SceneAsset> SceneAssets => Set<SceneAsset>();
    public DbSet<VideoVersion> VideoVersions => Set<VideoVersion>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAttempt> JobAttempts => Set<JobAttempt>();
    public DbSet<JobEvent> JobEvents => Set<JobEvent>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<ProviderRequest> ProviderRequests => Set<ProviderRequest>();
    public DbSet<PriceSnapshot> PriceSnapshots => Set<PriceSnapshot>();
    public DbSet<BudgetPeriod> BudgetPeriods => Set<BudgetPeriod>();
    public DbSet<BudgetReservationRecord> BudgetReservations => Set<BudgetReservationRecord>();
    public DbSet<CostEntry> CostEntries => Set<CostEntry>();
    public DbSet<Approval> Approvals => Set<Approval>();
    public DbSet<ExportPackage> ExportPackages => Set<ExportPackage>();
    public DbSet<Publication> Publications => Set<Publication>();
    public DbSet<MetricSnapshot> MetricSnapshots => Set<MetricSnapshot>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScheduleOccurrence> ScheduleOccurrences => Set<ScheduleOccurrence>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("app");

        ConfigureIdentityAndAccess(modelBuilder);
        ConfigureCatalog(modelBuilder);
        ConfigureCampaignsAndMedia(modelBuilder);
        ConfigureJobs(modelBuilder);
        ConfigureBilling(modelBuilder);
        ConfigureReviewAndAnalytics(modelBuilder);
        ConfigureSchedulingAndAudit(modelBuilder);

        ApplySnakeCaseColumnNames(modelBuilder);
    }

    private static void ConfigureIdentityAndAccess(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OwnerUser>(entity =>
        {
            entity.ToTable("owner_users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Username).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            entity.HasIndex(x => x.Username).IsUnique().HasDatabaseName("ux_owner_users_username");
        });
    }

    private static void ConfigureCatalog(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(entity =>
        {
            entity.ToTable("products");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalId).HasMaxLength(128);
            entity.Property(x => x.Title).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Market).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Locale).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(128);
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasIndex(x => new { x.Market, x.ExternalId })
                .IsUnique()
                .HasDatabaseName("ux_products_market_external_id")
                .HasFilter("external_id IS NOT NULL");
            entity.ToTable(t =>
            {
                t.HasCheckConstraint("ck_products_market", "market ~ '^[A-Z]{2}$'");
                t.HasCheckConstraint("ck_products_locale", "locale <> ''");
            });
        });

        modelBuilder.Entity<ProductClaim>(entity =>
        {
            entity.ToTable("product_claims");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Text).HasMaxLength(4000).IsRequired();
            entity.Property(x => x.SourceReference).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ProductId, x.Status })
                .HasDatabaseName("ix_product_claims_product_status");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_product_claims_status",
                "status IN ('pending','approved','rejected','expired')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_product_claims_source",
                "source_reference <> ''"));
        });

        modelBuilder.Entity<ProductOffer>(entity =>
        {
            entity.ToTable("product_offers");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Market).HasMaxLength(16).IsRequired();
            entity.Property(x => x.ProductUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.Price).HasPrecision(18, 6);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.CommissionRate).HasPrecision(9, 6);
            entity.Property(x => x.Availability).HasMaxLength(64).IsRequired();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ProductId, x.Market })
                .HasDatabaseName("ix_product_offers_product_market");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_product_offers_commission_rate",
                "commission_rate >= 0 AND commission_rate <= 1"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_product_offers_price",
                "price >= 0"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_product_offers_currency",
                "currency ~ '^[A-Z]{3}$'"));
        });

        modelBuilder.Entity<Asset>(entity =>
        {
            entity.ToTable("assets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Sha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Mime).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RightsStatus).HasMaxLength(32).IsRequired();
            entity.Property(x => x.SourceReference).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Kind).HasConversion(EnumToDb<AssetKind>());
            entity.HasIndex(x => new { x.Sha256, x.Kind })
                .IsUnique()
                .HasDatabaseName("ux_assets_sha256_kind");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_assets_sha256",
                "sha256 ~ '^[0-9a-f]{64}$'"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_assets_kind",
                "kind IN ('image','audio','video','subtitle','thumbnail','manifest','archive')"));
        });

        modelBuilder.Entity<ProductAsset>(entity =>
        {
            entity.ToTable("product_assets");
            entity.HasKey(x => new { x.ProductId, x.AssetId });
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Channel>(entity =>
        {
            entity.ToTable("channels");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Platform).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Market).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Locale).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.CapabilitiesJson).HasColumnType("jsonb").IsRequired();
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_channels_status",
                "status IN ('pending_review','active','disabled')"));
        });
    }

    private static void ConfigureCampaignsAndMedia(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Campaign>(entity =>
        {
            entity.ToTable("campaigns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Locale).HasMaxLength(32).IsRequired();
            entity.Property(x => x.BudgetUsd).HasPrecision(18, 6);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Mode).HasConversion(EnumToDb<VideoMode>());
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Product>().WithMany().HasForeignKey(x => x.ProductId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_campaigns_mode",
                "mode IN ('template_only','hybrid_motion')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_campaigns_status",
                "status IN ('draft','active','paused','archived')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_campaigns_duration",
                "target_duration_ms BETWEEN 15000 AND 35000"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_campaigns_budget",
                "budget_usd >= 0"));
        });

        modelBuilder.Entity<ContentRevision>(entity =>
        {
            entity.ToTable("content_revisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ProductSnapshotJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ScriptJson).HasColumnType("jsonb");
            entity.Property(x => x.ScriptHash).HasMaxLength(64);
            entity.Property(x => x.PromptVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ModelId).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.CampaignId, x.Revision })
                .IsUnique()
                .HasDatabaseName("ux_content_revisions_campaign_revision");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_content_revisions_status",
                "status IN ('draft','validated','blocked','archived')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_content_revisions_revision",
                "revision > 0"));
        });

        modelBuilder.Entity<SceneAsset>(entity =>
        {
            entity.ToTable("scene_assets");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SceneId).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Role).HasMaxLength(32).IsRequired();
            entity.HasOne<ContentRevision>().WithMany().HasForeignKey(x => x.ContentRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Asset>().WithMany().HasForeignKey(x => x.AssetId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ContentRevisionId, x.SceneId, x.Role })
                .IsUnique()
                .HasDatabaseName("ux_scene_assets_revision_scene_role");
        });

        modelBuilder.Entity<VideoVersion>(entity =>
        {
            entity.ToTable("video_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ManifestJson).HasColumnType("jsonb");
            entity.Property(x => x.ManifestSha256).HasMaxLength(64);
            entity.Property(x => x.QaJson).HasColumnType("jsonb");
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasOne<ContentRevision>().WithMany().HasForeignKey(x => x.ContentRevisionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Asset>().WithMany().HasForeignKey(x => x.OutputAssetId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ContentRevisionId, x.VersionNo })
                .IsUnique()
                .HasDatabaseName("ux_video_versions_revision_version");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_video_versions_status",
                "status IN ('draft','building','qa_failed','awaiting_review','approved','rejected','blocked','cancelled')"));
        });
    }

    private static void ConfigureJobs(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Job>(entity =>
        {
            entity.ToTable("jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.JobType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.DedupeKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.Property(x => x.LeaseOwner).HasMaxLength(128);
            entity.Property(x => x.LastErrorCode).HasMaxLength(64);
            entity.Property(x => x.Lane).HasConversion(EnumToDb<JobLane>());
            entity.HasIndex(x => x.DedupeKey)
                .IsUnique()
                .HasDatabaseName("ux_jobs_dedupe_key");
            entity.HasIndex(x => new { x.Lane, x.Priority, x.RunAfter, x.CreatedAt })
                .HasDatabaseName("ix_jobs_claimable")
                .HasFilter("status IN ('queued', 'retry_wait')");
            entity.HasIndex(x => new { x.Status, x.LeaseUntil })
                .HasDatabaseName("ix_jobs_status_lease");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_jobs_status",
                "status IN ('queued','running','succeeded','retry_wait','paused','failed','cancelled','blocked')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_jobs_lane",
                "lane IN ('control','heavy_local','cloud_submit','cloud_poll','download')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_jobs_attempts",
                "attempts >= 0 AND max_attempts > 0"));
        });

        modelBuilder.Entity<JobAttempt>(entity =>
        {
            entity.ToTable("job_attempts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ErrorCode).HasMaxLength(64);
            entity.Property(x => x.TraceId).HasMaxLength(128);
            entity.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.JobId, x.AttemptNo })
                .IsUnique()
                .HasDatabaseName("ux_job_attempts_job_attempt");
        });

        modelBuilder.Entity<JobEvent>(entity =>
        {
            entity.ToTable("job_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DetailsJson).HasColumnType("jsonb").IsRequired();
            entity.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.JobId, x.OccurredAt })
                .HasDatabaseName("ix_job_events_job_occurred");
        });

        modelBuilder.Entity<OutboxEvent>(entity =>
        {
            entity.ToTable("outbox_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EventType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.PublishedAt, x.CreatedAt })
                .HasDatabaseName("ix_outbox_unpublished")
                .HasFilter("published_at IS NULL");
        });

        modelBuilder.Entity<ProviderRequest>(entity =>
        {
            entity.ToTable("provider_requests");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ClientOperationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.RequestHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ProviderRequestId).HasMaxLength(256);
            entity.Property(x => x.StatusUrl).HasMaxLength(2048);
            entity.Property(x => x.ResponseUrl).HasMaxLength(2048);
            entity.Property(x => x.CancelUrl).HasMaxLength(2048);
            entity.Property(x => x.Status).HasMaxLength(32).IsRequired();
            entity.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => x.ClientOperationId)
                .IsUnique()
                .HasDatabaseName("ux_provider_requests_client_operation");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_provider_requests_status",
                "status IN ('prepared','submitting','submitted','in_progress','succeeded','failed_confirmed','submission_unknown','cancellation_requested','cancelled_confirmed')"));
        });
    }

    private static void ConfigureBilling(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PriceSnapshot>(entity =>
        {
            entity.ToTable("price_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ConfigHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Unit).HasMaxLength(64).IsRequired();
            entity.Property(x => x.UnitPriceUsd).HasPrecision(18, 6);
            entity.Property(x => x.SourceUrl).HasMaxLength(2048).IsRequired();
            entity.HasIndex(x => new { x.Provider, x.Model, x.ConfigHash, x.ValidUntil })
                .HasDatabaseName("ix_price_snapshots_lookup");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_price_snapshots_price",
                "unit_price_usd >= 0"));
        });

        modelBuilder.Entity<BudgetPeriod>(entity =>
        {
            entity.ToTable("budget_periods");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ScopeType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.LimitUsd).HasPrecision(18, 6);
            entity.Property(x => x.ReservedUsd).HasPrecision(18, 6);
            entity.Property(x => x.SpentUsd).HasPrecision(18, 6);
            entity.HasIndex(x => new { x.ScopeType, x.ScopeId, x.PeriodStart, x.PeriodEnd })
                .IsUnique()
                .HasDatabaseName("ux_budget_periods_scope_period");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_budget_periods_scope",
                "scope_type IN ('monthly','daily','campaign','video')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_budget_periods_amounts",
                "limit_usd >= 0 AND reserved_usd >= 0 AND spent_usd >= 0"));
        });

        modelBuilder.Entity<BudgetReservationRecord>(entity =>
        {
            entity.ToTable("budget_reservations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OperationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.Provider).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Model).HasMaxLength(256).IsRequired();
            entity.Property(x => x.ApprovalId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ReservedUsd).HasPrecision(18, 6);
            entity.Property(x => x.SettledUsd).HasPrecision(18, 6);
            entity.Property(x => x.AllocationsJson).HasColumnType("jsonb").IsRequired();
            entity.Property(x => x.ReleaseReason).HasMaxLength(512);
            entity.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<PriceSnapshot>().WithMany().HasForeignKey(x => x.PriceSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.OperationId)
                .IsUnique()
                .HasDatabaseName("ux_budget_reservations_operation");
            entity.HasIndex(x => new { x.ApprovalId, x.Status })
                .HasDatabaseName("ix_budget_reservations_approval_status");
            entity.HasIndex(x => x.CampaignId)
                .HasDatabaseName("ix_budget_reservations_campaign");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_budget_reservations_status",
                "status IN ('reserved','unknown','settled','released')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_budget_reservations_amounts",
                "reserved_usd >= 0 AND (settled_usd IS NULL OR settled_usd >= 0)"));
        });

        modelBuilder.Entity<CostEntry>(entity =>
        {
            entity.ToTable("cost_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.OperationId).HasMaxLength(256).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(256).IsRequired();
            entity.Property(x => x.EntryType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.AmountUsd).HasPrecision(18, 6);
            entity.Property(x => x.Certainty).HasMaxLength(32).IsRequired();
            entity.HasOne<PriceSnapshot>().WithMany().HasForeignKey(x => x.PriceSnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasDatabaseName("ux_cost_entries_idempotency_key");
            entity.HasIndex(x => x.OperationId)
                .HasDatabaseName("ix_cost_entries_operation");
            entity.HasIndex(x => x.ReservationId)
                .HasDatabaseName("ix_cost_entries_reservation");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_cost_entries_type",
                "entry_type IN ('reserve','settle','release','adjustment','refund')"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_cost_entries_certainty",
                "certainty IN ('confirmed','estimated')"));
        });
    }

    private static void ConfigureReviewAndAnalytics(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Approval>(entity =>
        {
            entity.ToTable("approvals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ManifestSha256).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Decision).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ChecklistJson).HasColumnType("jsonb").IsRequired();
            entity.HasOne<VideoVersion>().WithMany().HasForeignKey(x => x.VideoVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.VideoVersionId, x.ManifestSha256 })
                .HasDatabaseName("ix_approvals_video_manifest");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_approvals_decision",
                "decision IN ('approved','rejected')"));
        });

        modelBuilder.Entity<ExportPackage>(entity =>
        {
            entity.ToTable("export_packages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PackageHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(512).IsRequired();
            entity.HasOne<VideoVersion>().WithMany().HasForeignKey(x => x.VideoVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.VideoVersionId, x.ChannelId, x.PackageHash })
                .IsUnique()
                .HasDatabaseName("ux_export_packages_video_channel_hash");
        });

        modelBuilder.Entity<Publication>(entity =>
        {
            entity.ToTable("publications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.PostUrl).HasMaxLength(2048).IsRequired();
            entity.Property(x => x.ExternalPostId).HasMaxLength(256);
            entity.Property(x => x.ProductLinkStatus).HasMaxLength(32).IsRequired();
            entity.Property(x => x.EvidenceReference).HasMaxLength(1024);
            entity.HasOne<VideoVersion>().WithMany().HasForeignKey(x => x.VideoVersionId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Channel>().WithMany().HasForeignKey(x => x.ChannelId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_publications_product_link",
                "product_link_status IN ('unknown','verified','broken','not_applicable')"));
        });

        modelBuilder.Entity<MetricSnapshot>(entity =>
        {
            entity.ToTable("metric_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Source).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CommissionPending).HasPrecision(18, 6);
            entity.Property(x => x.CommissionSettled).HasPrecision(18, 6);
            entity.Property(x => x.Currency).HasMaxLength(3).IsRequired();
            entity.Property(x => x.ImportKey).HasMaxLength(256).IsRequired();
            entity.HasOne<Publication>().WithMany().HasForeignKey(x => x.PublicationId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.ImportKey)
                .IsUnique()
                .HasDatabaseName("ux_metric_snapshots_import_key");
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_metric_snapshots_currency",
                "currency = '' OR currency ~ '^[A-Z]{3}$'"));
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_metric_snapshots_counts",
                "(views IS NULL OR views >= 0) AND (product_clicks IS NULL OR product_clicks >= 0) AND (orders IS NULL OR orders >= 0)"));
        });
    }

    private static void ConfigureSchedulingAndAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Schedule>(entity =>
        {
            entity.ToTable("schedules");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Timezone).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DaysOfWeek).HasColumnType("integer[]").IsRequired();
            entity.Property(x => x.Version).IsConcurrencyToken();
            entity.HasOne<Campaign>().WithMany().HasForeignKey(x => x.CampaignId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint(
                "ck_schedules_max_jobs",
                "max_jobs_per_day > 0"));
        });

        modelBuilder.Entity<ScheduleOccurrence>(entity =>
        {
            entity.ToTable("schedule_occurrences");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.SlotKey).HasMaxLength(64).IsRequired();
            entity.HasOne<Schedule>().WithMany().HasForeignKey(x => x.ScheduleId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<Job>().WithMany().HasForeignKey(x => x.JobId)
                .OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.ScheduleId, x.SlotKey })
                .IsUnique()
                .HasDatabaseName("ux_schedule_occurrences_schedule_slot");
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Actor).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Action).HasMaxLength(128).IsRequired();
            entity.Property(x => x.TargetType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DetailsJson).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(x => new { x.TargetType, x.TargetId })
                .HasDatabaseName("ix_audit_events_target");
            entity.HasIndex(x => x.CreatedAt)
                .HasDatabaseName("ix_audit_events_created");
        });
    }

    private static ValueConverter<TEnum, string> EnumToDb<TEnum>()
        where TEnum : struct, Enum
        => new(
            value => ToSnakeCase(value.ToString()),
            value => Enum.Parse<TEnum>(value.Replace("_", string.Empty, StringComparison.Ordinal), ignoreCase: true));

    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> UtcTimestampConverter = new(
        value => value.ToUniversalTime(),
        value => value.ToUniversalTime());

    private static string ToSnakeCase(string value)
        => Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();

    /// <summary>
    /// EF Core does not pluralise or snake_case automatically. Table names are declared
    /// explicitly; this convention makes every column and generated index snake_case.
    /// </summary>
    private static void ApplySnakeCaseColumnNames(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));

                if (property.ClrType == typeof(DateTimeOffset) ||
                    property.ClrType == typeof(DateTimeOffset?))
                {
                    // PostgreSQL timestamptz stores an instant; Npgsql accepts only UTC
                    // DateTimeOffset values. Normalise at the EF boundary so callers may
                    // pass an instant with a +07:00 offset.
                    property.SetValueConverter(UtcTimestampConverter);
                }
            }
        }
    }
}
