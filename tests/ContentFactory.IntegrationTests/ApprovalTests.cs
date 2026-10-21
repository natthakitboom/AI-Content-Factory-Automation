using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Jobs;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Review;
using ContentFactory.IntegrationTests.Api;
using ContentFactory.IntegrationTests.Infrastructure;
using ContentFactory.Worker.Handlers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ContentFactory.IntegrationTests;

/// <summary>
/// T16: an approval is bound to exactly one manifest hash, comes only from the signed-in
/// owner, and any caption edit produces a new version that must be reviewed again.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class ApprovalTests
{
    private readonly ApiTestFixture _fixture;

    public ApprovalTests(ApiTestFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task WrongManifestHashRejected()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await ReviewSeed.CreateAsync(db);
        var owner = await OwnerIdAsync(db);

        var error = await Assert.ThrowsAsync<ApprovalStaleException>(() =>
            Service(scope).RecordAsync(
                new ReviewDecision(
                    seeded.VersionId, new string('0', 64), true, ReviewSeed.FullChecklist(), null),
                owner,
                default));

        Assert.Equal("APPROVAL_HASH_MISMATCH", error.Code);
        Assert.False(await db.Approvals.AnyAsync(x => x.VideoVersionId == seeded.VersionId));
        Assert.Equal("awaiting_review", await StatusAsync(db, seeded.VersionId));
    }

    [Fact]
    public async Task FailedQaCannotApprove()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var owner = await OwnerIdAsync(db);

        foreach (var (status, qaPassed, code) in new[]
        {
            ("qa_failed", false, "REVIEW_NOT_READY"),
            ("blocked", false, "REVIEW_NOT_READY"),
            ("building", true, "REVIEW_NOT_READY"),
            ("awaiting_review", false, "REVIEW_QA_NOT_PASSED")
        })
        {
            var seeded = await ReviewSeed.CreateAsync(db, status, qaPassed);
            var error = await Assert.ThrowsAsync<ConflictException>(() =>
                Service(scope).RecordAsync(
                    new ReviewDecision(
                        seeded.VersionId, seeded.ManifestSha256, true, ReviewSeed.FullChecklist(), null),
                    owner,
                    default));
            Assert.Equal(code, error.Code);
            Assert.False(await db.Approvals.AnyAsync(x => x.VideoVersionId == seeded.VersionId));
        }
    }

    [Fact]
    public async Task IncompleteChecklistRejected()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await ReviewSeed.CreateAsync(db);
        var owner = await OwnerIdAsync(db);

        var missing = ReviewSeed.FullChecklist();
        missing.Remove("rights_confirmed");
        var unchecked_ = ReviewSeed.FullChecklist();
        unchecked_["offer_checked"] = false;

        foreach (var checklist in new[] { missing, unchecked_, new Dictionary<string, bool>() })
        {
            var error = await Assert.ThrowsAsync<ValidationException>(() =>
                Service(scope).RecordAsync(
                    new ReviewDecision(seeded.VersionId, seeded.ManifestSha256, true, checklist, null),
                    owner,
                    default));
            Assert.Equal("REVIEW_CHECKLIST_INCOMPLETE", error.Code);
        }

        Assert.Equal("awaiting_review", await StatusAsync(db, seeded.VersionId));
    }

    [Fact]
    public async Task ApprovalIsStoredOnceBoundToTheExactHash()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await ReviewSeed.CreateAsync(db);
        var owner = await OwnerIdAsync(db);
        var decision = new ReviewDecision(
            seeded.VersionId, seeded.ManifestSha256, true, ReviewSeed.FullChecklist(), "ok");

        await Service(scope).RecordAsync(decision, owner, default);
        var second = await Assert.ThrowsAsync<ConflictException>(() =>
            Service(scope).RecordAsync(decision, owner, default));

        Assert.Equal("REVIEW_ALREADY_DECIDED", second.Code);
        var approval = await db.Approvals.AsNoTracking().SingleAsync(x => x.VideoVersionId == seeded.VersionId);
        Assert.Equal(seeded.ManifestSha256, approval.ManifestSha256);
        Assert.Equal(owner, approval.ReviewerId);
        Assert.Equal("approved", approval.Decision);
        Assert.Equal("approved", await StatusAsync(db, seeded.VersionId));
    }

    [Fact]
    public async Task RejectionNeedsAComment()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await ReviewSeed.CreateAsync(db);
        var owner = await OwnerIdAsync(db);

        var error = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(scope).RecordAsync(
                new ReviewDecision(
                    seeded.VersionId, seeded.ManifestSha256, false, new Dictionary<string, bool>(), " "),
                owner,
                default));
        Assert.Equal("REVIEW_COMMENT_REQUIRED", error.Code);

        await Service(scope).RecordAsync(
            new ReviewDecision(
                seeded.VersionId, seeded.ManifestSha256, false, new Dictionary<string, bool>(), "logo is wrong"),
            owner,
            default);
        Assert.Equal("rejected", await StatusAsync(db, seeded.VersionId));
    }

    [Fact]
    public async Task EditedCaptionInvalidatesApproval()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await ReviewSeed.CreateAsync(db);
        var owner = await OwnerIdAsync(db);
        await Service(scope).RecordAsync(
            new ReviewDecision(
                seeded.VersionId, seeded.ManifestSha256, true, ReviewSeed.FullChecklist(), null),
            owner,
            default);

        var edit = await Service(scope).EditCaptionAsync(
            seeded.VersionId, "benefit", "ผิวชุ่มชื้นและนุ่มสบาย", owner, default);
        var replay = await Service(scope).EditCaptionAsync(
            seeded.VersionId, "benefit", "ผิวชุ่มชื้นและนุ่มสบาย", owner, default);

        Assert.Equal(edit.JobId, replay.JobId);
        Assert.Equal(edit.RevisionId, replay.RevisionId);
        Assert.NotEqual(seeded.RevisionId, edit.RevisionId);

        var job = await db.Jobs.AsNoTracking().SingleAsync(x => x.Id == edit.JobId);
        Assert.Equal("compile_timeline", job.JobType);

        // Run the real compile stage on the queued job: the new manifest hash differs, and
        // the earlier approval does not carry over to it.
        var compile = new CompileTimelineHandler(
            db, new TimelineCompiler(new TimelineOptions { FontFingerprint = ReviewSeed.FontFingerprint }));
        var outcome = await compile.ExecuteAsync(
            new LeasedJob(job.Id, job.JobType, job.PayloadJson, 1, "test", 1, DateTimeOffset.UtcNow.AddMinutes(1)),
            default);
        Assert.Equal("succeeded", outcome.Status);

        var newVersion = await db.VideoVersions.AsNoTracking()
            .SingleAsync(x => x.ContentRevisionId == edit.RevisionId);
        Assert.NotEqual(seeded.ManifestSha256, newVersion.ManifestSha256);
        Assert.False(await db.Approvals.AnyAsync(x => x.VideoVersionId == newVersion.Id));
        Assert.Contains("ผิวชุ่มชื้นและนุ่มสบาย", newVersion.ManifestJson);
        Assert.Equal("approved", await StatusAsync(db, seeded.VersionId));

        var carryOver = await Assert.ThrowsAnyAsync<Exception>(() =>
            Service(scope).RecordAsync(
                new ReviewDecision(
                    newVersion.Id, seeded.ManifestSha256, true, ReviewSeed.FullChecklist(), null),
                owner,
                default));
        Assert.False(await db.Approvals.AnyAsync(x => x.VideoVersionId == newVersion.Id));
        Assert.True(carryOver is ConflictException or ApprovalStaleException);
    }

    [Fact]
    public async Task CaptionEditRejectsUnsafeOrUnknownInput()
    {
        await using var scope = _fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var seeded = await ReviewSeed.CreateAsync(db);
        var owner = await OwnerIdAsync(db);

        var markup = await Assert.ThrowsAsync<ValidationException>(() =>
            Service(scope).EditCaptionAsync(seeded.VersionId, "benefit", "{\\an8}<b>x</b>", owner, default));
        var unknown = await Assert.ThrowsAsync<NotFoundException>(() =>
            Service(scope).EditCaptionAsync(seeded.VersionId, "nope", "ข้อความ", owner, default));

        Assert.NotEmpty(markup.Code);
        Assert.Equal("REVIEW_SCENE_NOT_FOUND", unknown.Code);
    }

    [Fact]
    public async Task ReviewerIdentityComesFromSession()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        Guid versionId;
        string hash;
        Guid owner;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var seeded = await ReviewSeed.CreateAsync(db);
            versionId = seeded.VersionId;
            hash = seeded.ManifestSha256;
            owner = await OwnerIdAsync(db);
        }

        var response = await client.PostAsJsonAsync(
            $"/api/v1/videos/{versionId}/reviews",
            new
            {
                manifestSha256 = hash,
                approved = true,
                checklist = ReviewSeed.FullChecklist(),
                comment = "looks right",
                reviewerId = Guid.NewGuid()
            });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        await using var verify = _fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verify.ServiceProvider.GetRequiredService<AppDbContext>();
        var approval = await verifyDb.Approvals.AsNoTracking().SingleAsync(x => x.VideoVersionId == versionId);
        Assert.Equal(owner, approval.ReviewerId);
    }

    [Fact]
    public async Task ReviewEndpointsRequireLoginAndExposeNoApproveAll()
    {
        using var anonymous = _fixture.CreateClient();
        var versionId = Guid.NewGuid();

        var get = await anonymous.GetAsync($"/api/v1/videos/{versionId}");
        Assert.True(get.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        var client = await _fixture.CreateLoggedInClientAsync();
        var approveAll = await client.PostAsJsonAsync(
            "/api/v1/videos/reviews/approve-all", new { approveAll = true });
        Assert.True(approveAll.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed);
    }

    [Fact]
    public async Task VideoDetailShowsFactsAndMotionWarning()
    {
        var client = await _fixture.CreateLoggedInClientAsync();
        Guid versionId;
        await using (var scope = _fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            versionId = (await ReviewSeed.CreateAsync(db)).VersionId;
        }

        var detail = await client.GetFromJsonAsync<JsonElement>($"/api/v1/videos/{versionId}");

        Assert.Equal("awaiting_review", detail.GetProperty("status").GetString());
        Assert.Equal(7, detail.GetProperty("checklist").GetArrayLength());
        Assert.True(detail.GetProperty("scenes").GetArrayLength() >= 3);
        Assert.False(detail.GetProperty("usesAiMotion").GetBoolean());
        Assert.True(detail.GetProperty("claims").GetArrayLength() >= 1);
        Assert.Equal(0m, detail.GetProperty("cost").GetProperty("reservedUsd").GetDecimal());
        Assert.StartsWith("/api/v1/assets/", detail.GetProperty("mediaUrl").GetString());
        Assert.DoesNotContain("password", detail.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ReviewService Service(AsyncServiceScope scope)
        => scope.ServiceProvider.GetRequiredService<ReviewService>();

    private static async Task<Guid> OwnerIdAsync(AppDbContext db)
        => await db.OwnerUsers.AsNoTracking()
            .Where(x => x.Username == ApiTestFixture.Username)
            .Select(x => x.Id)
            .SingleAsync();

    private static async Task<string> StatusAsync(AppDbContext db, Guid versionId)
    {
        db.ChangeTracker.Clear();
        return await db.VideoVersions.AsNoTracking()
            .Where(x => x.Id == versionId)
            .Select(x => x.Status)
            .SingleAsync();
    }
}
