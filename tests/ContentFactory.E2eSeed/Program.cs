using System.Security.Cryptography;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using ContentFactory.Infrastructure.Review;
using ContentFactory.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Test-only tool for the dashboard end-to-end run.
//   seed <root-dir> <out-file>   create a scratch cf_test_ database with real mock-pipeline output
//   drop <database>              remove that scratch database
// It refuses to touch anything but databases whose name starts with cf_test_.
const string Prefix = "cf_test_";

var admin = Environment.GetEnvironmentVariable("CF_TEST_POSTGRES")
    ?? throw new InvalidOperationException("CF_TEST_POSTGRES is not set.");

if (args.Length >= 2 && args[0] == "drop")
{
    RequireTestName(args[1]);
    NpgsqlConnection.ClearAllPools();
    await ExecuteAdminAsync(admin, $"DROP DATABASE IF EXISTS \"{args[1]}\" WITH (FORCE)");
    return 0;
}

if (args.Length < 3 || args[0] != "seed")
{
    Console.Error.WriteLine("Usage: seed <root-dir> <out-file> | drop <database>");
    return 2;
}

var root = Path.GetFullPath(args[1]);
var database = Prefix + "e2e_" + Guid.NewGuid().ToString("N")[..10];
await ExecuteAdminAsync(admin, $"CREATE DATABASE \"{database}\" TEMPLATE template0 ENCODING 'UTF8'");

var connection = new NpgsqlConnectionStringBuilder(admin) { Database = database, Pooling = true }.ConnectionString;
await using var db = new AppDbContext(
    new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options, TimeProvider.System);
await db.Database.MigrateAsync();

var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'a').Replace('/', 'b');
var owner = new OwnerUser
{
    Id = Guid.NewGuid(), Username = "owner", Active = true, SessionVersion = 1, CreatedAt = DateTimeOffset.UtcNow
};
owner.PasswordHash = new PasswordHasher<OwnerUser>().HashPassword(owner, password);
db.OwnerUsers.Add(owner);
await db.SaveChangesAsync();

var harness = new PipelineHarness(db, root);
var review = await ProduceAsync("E2E review product");
var captionEdit = await ProduceAsync("E2E caption product");
var export = await ProduceAsync("E2E export product");

// Approve the export candidate through the real service and give it a fresh offer.
var clock = TimeProvider.System;
var reviews = new ReviewService(db, new PostgresJobStore(db, clock), clock);
var exportVersion = await db.VideoVersions.AsNoTracking().SingleAsync(x => x.Id == export);
await reviews.RecordAsync(
    new ReviewDecision(export, exportVersion.ManifestSha256!, true, ReviewSeed.FullChecklist(), "e2e seed"),
    owner.Id,
    default);
var exportProduct = await (
        from v in db.VideoVersions
        join r in db.ContentRevisions on v.ContentRevisionId equals r.Id
        join c in db.Campaigns on r.CampaignId equals c.Id
        where v.Id == export
        select c.ProductId)
    .SingleAsync();
db.ProductOffers.Add(new ProductOffer
{
    Id = Guid.NewGuid(), ProductId = exportProduct, Market = "TH", ProductUrl = "https://shop.example.test/p/e2e",
    Price = 199m, Currency = "THB", CommissionRate = 0.1m, Availability = "in_stock",
    CheckedAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
});

// A stale price, held and spent money, and an uncertain cloud job for the operator pages.
var campaignId = await db.Campaigns.Select(x => x.Id).FirstAsync();
var now = DateTimeOffset.UtcNow;
var price = new PriceSnapshot
{
    Id = Guid.NewGuid(), Provider = "fal", Model = "e2e-model", ConfigHash = "e2e", Unit = "request",
    UnitPriceUsd = 0.10m, SourceUrl = "https://fal.ai/pricing", VerifiedAt = now.AddDays(-3), ValidUntil = now.AddDays(-2)
};
db.PriceSnapshots.Add(price);
foreach (var (status, reserved, settled) in new[] { ("reserved", 0.12m, (decimal?)null), ("settled", 0.12m, 0.10m) })
{
    db.BudgetReservations.Add(new BudgetReservationRecord
    {
        Id = Guid.NewGuid(), OperationId = "e2e-" + Guid.NewGuid().ToString("N"), CampaignId = campaignId,
        VideoScopeId = Guid.NewGuid(), Provider = "fal", Model = "e2e-model", PriceSnapshotId = price.Id,
        ApprovalId = "e2e", ReservedUsd = reserved, SettledUsd = settled, Status = status,
        AllocationsJson = "[]", CreatedAt = now
    });
}

var unknownJob = Guid.NewGuid();
db.Jobs.Add(new Job
{
    Id = unknownJob, JobType = "submit_video", PayloadJson = "{}", DedupeKey = "e2e-unknown-" + unknownJob.ToString("N"),
    Lane = JobLane.CloudSubmit, Status = "failed", LastErrorCode = "SUBMISSION_UNKNOWN",
    RunAfter = now, CreatedAt = now, UpdatedAt = now
});
await db.SaveChangesAsync();

await File.WriteAllTextAsync(
    args[2],
    JsonSerializer.Serialize(
        new
        {
            database,
            connection,
            username = owner.Username,
            password,
            videos = new { review, captionEdit, export },
            unknownJob
        }));
Console.WriteLine($"Seeded {database}.");
return 0;

async Task<Guid> ProduceAsync(string title)
{
    var campaign = await harness.SeedCampaignAsync(VideoMode.TemplateOnly, title);
    var run = await harness.EnqueueAsync(campaign);
    await harness.PumpAsync();
    var problem = await harness.UnfinishedWorkAsync();
    if (problem is not null)
    {
        throw new InvalidOperationException(problem);
    }

    return await db.VideoVersions.AsNoTracking()
        .Where(x => x.ContentRevisionId == run.RevisionId)
        .Select(x => x.Id)
        .SingleAsync();
}

static void RequireTestName(string name)
{
    if (!name.StartsWith(Prefix, StringComparison.Ordinal) || name.Any(c => !(char.IsLetterOrDigit(c) || c == '_')))
    {
        throw new InvalidOperationException("Only cf_test_ databases may be created or dropped.");
    }
}

static async Task ExecuteAdminAsync(string adminConnection, string sql)
{
    await using var connection = new NpgsqlConnection(adminConnection);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
