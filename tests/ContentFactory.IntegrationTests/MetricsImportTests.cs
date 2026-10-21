using System.Text;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Analytics;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.IntegrationTests;

[CollectionDefinition(Name)]
public sealed class MetricsCollection : ICollectionFixture<PostgresSchemaFixture>
{
    public const string Name = "postgres-metrics";
}

/// <summary>
/// T19: imports are idempotent and traceable, corrected rows reconcile in place with an
/// audit trail, and missing data stays missing instead of becoming zero.
/// </summary>
[Collection(MetricsCollection.Name)]
public sealed class MetricsImportTests
{
    private const string PostA = "https://www.tiktok.com/@owner/video/1";
    private const string PostB = "https://www.tiktok.com/@owner/video/2";
    private readonly PostgresSchemaFixture _fixture;

    public MetricsImportTests(PostgresSchemaFixture fixture)
    {
        _fixture = fixture;
    }

    private static ImportMapping Mapping(string source = "tiktok-export") => new(
        Source: source,
        Currency: "THB",
        AsOf: new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero),
        AsOfColumn: null,
        PublicationColumn: "post_url",
        PublicationKind: "post_url",
        ViewsColumn: "views",
        ProductClicksColumn: "clicks",
        OrdersColumn: "orders",
        CommissionPendingColumn: "pending",
        CommissionSettledColumn: "settled",
        CommissionReturnedColumn: "returned");

    [Fact]
    public async Task DuplicateImportDoesNotDoubleCount()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var csv = $"post_url,views,clicks,orders,pending,settled,returned\n{PostA},100,10,1,5,20,0\n{PostB},50,5,0,1,2,0\n";
        var importer = new MetricsImporter(db, TimeProvider.System);

        var first = await importer.ImportAsync(Stream(csv), Mapping(), "owner", default);
        var rowCountAfterFirstImport = await db.MetricSnapshots.CountAsync();
        var second = await importer.ImportAsync(Stream(csv), Mapping(), "owner", default);

        Assert.Equal(2, first.Inserted);
        Assert.Equal(0, second.Inserted);
        Assert.Equal(2, second.Unchanged);
        Assert.Equal(rowCountAfterFirstImport, await db.MetricSnapshots.CountAsync());
        var overview = await Single(importer);
        Assert.Equal(150, overview.Views);
    }

    [Fact]
    public async Task CorrectedRowReconcilesInPlaceAndIsAudited()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var importer = new MetricsImporter(db, TimeProvider.System);
        await importer.ImportAsync(
            Stream($"post_url,views,clicks,orders,pending,settled,returned\n{PostA},100,10,1,5,20,0\n"),
            Mapping(), "owner", default);

        var corrected = await importer.ImportAsync(
            Stream($"post_url,views,clicks,orders,pending,settled,returned\n{PostA},120,10,1,5,20,0\n"),
            Mapping(), "owner", default);

        Assert.Equal(1, corrected.Updated);
        Assert.Equal(0, corrected.Inserted);
        Assert.Equal(1, await db.MetricSnapshots.CountAsync());
        Assert.Equal(120, (await Single(importer)).Views);
        Assert.True(await db.AuditEvents.AnyAsync(x => x.Action == "metrics.row_updated"));
    }

    [Fact]
    public async Task ReturnsReduceSettledCommission()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var importer = new MetricsImporter(db, TimeProvider.System);

        await importer.ImportAsync(
            Stream($"post_url,views,clicks,orders,pending,settled,returned\n{PostA},100,10,2,5,100,30\n"),
            Mapping(), "owner", default);

        var thb = Assert.Single((await Single(importer)).Commission);
        Assert.Equal(70m, thb.Settled);
        Assert.Equal(5m, thb.Pending);
    }

    [Fact]
    public async Task MissingClicksRemainNullEndToEnd()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var importer = new MetricsImporter(db, TimeProvider.System);
        var mapping = Mapping() with
        {
            ProductClicksColumn = null, OrdersColumn = null,
            CommissionPendingColumn = null, CommissionSettledColumn = null, CommissionReturnedColumn = null
        };

        await importer.ImportAsync(Stream($"post_url,views\n{PostA},300\n"), mapping, "owner", default);

        var row = await db.MetricSnapshots.AsNoTracking().SingleAsync();
        Assert.Null(row.ProductClicks);
        Assert.Null(row.Orders);
        Assert.Null(row.CommissionSettled);
        var overview = await Single(importer);
        Assert.Equal(300, overview.Views);
        Assert.Null(overview.Ctr);
        Assert.Null(overview.ProductClicks);
    }

    [Fact]
    public async Task BadRowsAreReportedAndGoodRowsStillImport()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var importer = new MetricsImporter(db, TimeProvider.System);
        var csv = "post_url,views,clicks,orders,pending,settled,returned\n"
            + $"{PostA},100,10,1,5,20,0\n"
            + "https://www.tiktok.com/@owner/video/unknown,5,1,0,0,0,0\n"
            + $"{PostB},-4,1,0,0,0,0\n"
            + $"{PostB},abc,1,0,0,0,0\n"
            + "=HYPERLINK(\"http://evil\"),1,1,0,0,0,0\n";

        var report = await importer.ImportAsync(Stream(csv), Mapping(), "owner", default);

        Assert.Equal(1, report.Inserted);
        Assert.Equal(4, report.Rejected);
        Assert.Contains(report.Issues, x => x.Code == "METRICS_PUBLICATION_UNKNOWN");
        Assert.Contains(report.Issues, x => x.Code == "METRICS_NUMBER_INVALID");
        Assert.Equal(1, await db.MetricSnapshots.CountAsync());
    }

    [Fact]
    public async Task MappingMustNameRealColumnsAndACurrencyForMoney()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var importer = new MetricsImporter(db, TimeProvider.System);

        var missingColumn = await Assert.ThrowsAsync<ValidationException>(() =>
            importer.ImportAsync(
                Stream($"post_url,views\n{PostA},1\n"),
                Mapping() with { ViewsColumn = "impressions" }, "owner", default));
        var noCurrency = await Assert.ThrowsAsync<ValidationException>(() =>
            importer.ImportAsync(
                Stream($"post_url,views,pending\n{PostA},1,2\n"),
                Mapping() with
                {
                    Currency = null, ProductClicksColumn = null, OrdersColumn = null,
                    CommissionSettledColumn = null, CommissionReturnedColumn = null
                },
                "owner", default));

        Assert.Equal("METRICS_COLUMN_MISSING", missingColumn.Code);
        Assert.Equal("METRICS_CURRENCY_REQUIRED", noCurrency.Code);
    }

    [Fact]
    public async Task DifferentSourcesStaySeparateSnapshots()
    {
        await using var db = _fixture.CreateContext();
        await SeedAsync(db);
        var importer = new MetricsImporter(db, TimeProvider.System);
        var csv = $"post_url,views,clicks,orders,pending,settled,returned\n{PostA},100,10,1,5,20,0\n";

        await importer.ImportAsync(Stream(csv), Mapping("tiktok-export"), "owner", default);
        await importer.ImportAsync(Stream(csv), Mapping("affiliate-report"), "owner", default);

        Assert.Equal(2, await db.MetricSnapshots.CountAsync());
        Assert.Equal(
            ["affiliate-report", "tiktok-export"],
            (await importer.OverviewBySourceAsync(default)).Select(x => x.Source));
    }

    private static async Task<MetricsOverview> Single(MetricsImporter importer)
        => Assert.Single(await importer.OverviewBySourceAsync(default)).Overview;

    private static MemoryStream Stream(string text) => new(new UTF8Encoding(false).GetBytes(text));

    private static async Task SeedAsync(AppDbContext db)
    {
        // Tests in this class share one database; start every test from a clean metric state.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM app.metric_snapshots");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM app.publications");
        var seeded = await ReviewSeed.CreateAsync(db, "approved");
        var channelId = await db.Campaigns.Where(x => x.Id == seeded.CampaignId)
            .Select(x => x.ChannelId).SingleAsync();
        var now = DateTimeOffset.UtcNow;
        foreach (var (url, id) in new[] { (PostA, "1"), (PostB, "2") })
        {
            db.Publications.Add(new Publication
            {
                Id = Guid.NewGuid(), VideoVersionId = seeded.VersionId, ChannelId = channelId,
                PostUrl = url, ExternalPostId = id, PublishedAt = now, ProductLinkStatus = "unknown", CreatedAt = now
            });
        }

        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
    }
}
