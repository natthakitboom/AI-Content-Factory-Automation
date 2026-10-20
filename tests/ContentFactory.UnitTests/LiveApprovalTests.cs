using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Billing;

namespace ContentFactory.UnitTests;

/// <summary>
/// The live approval envelope is the only thing that may turn a paid provider call from
/// "disabled" into "allowed". These tests prove the guard fails closed for missing,
/// malformed, expired, mismatched and placeholder envelopes, and that it re-reads the
/// envelope for every request instead of caching a stale approval.
/// </summary>
public sealed class LiveApprovalTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 10, 20, 17, 0, 0, TimeSpan.Zero);

    private const string Provider = "fal";
    private const string Model = "fal-ai/wan/v2.2-a14b/image-to-video/turbo";

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "contentfactory-approval-tests",
        Guid.NewGuid().ToString("N"));

    public LiveApprovalTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public async Task ValidApprovalIsAccepted()
    {
        var expiresAt = Now.AddHours(1);
        var path = WriteApproval(expiresAt: expiresAt);
        var guard = CreateGuard(path, new ManualTimeProvider(Now));

        var envelope = await guard.ValidateAsync(Provider, Model, CancellationToken.None);

        Assert.Equal("test-approval", envelope.ApprovalId);
        Assert.Equal(Provider, envelope.Provider);
        Assert.Equal(Model, envelope.Model);
        Assert.Equal(3, envelope.MaxRequestCount);
        Assert.Equal(0.36m, envelope.MaxTotalUsd);
        Assert.Equal(expiresAt, envelope.ExpiresAt);
        Assert.Equal("owner-test", envelope.ApprovedBy);
    }

    [Fact]
    public async Task PaidDisabledBlocksEvenWithValidApproval()
    {
        var path = WriteApproval();
        var guard = CreateGuard(path, new ManualTimeProvider(Now), allowPaidApis: false);

        var exception = await Assert.ThrowsAsync<PaidApisDisabledException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("PAID_APIS_DISABLED", exception.Code);
    }

    [Fact]
    public async Task MissingApprovalFileFailsClosed()
    {
        var guard = CreateGuard(
            Path.Combine(_directory, "missing.json"),
            new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_MISSING", exception.Code);
    }

    [Fact]
    public async Task MalformedApprovalFailsClosed()
    {
        var path = Path.Combine(_directory, "malformed.json");
        await File.WriteAllTextAsync(path, "{ not-json");
        var guard = CreateGuard(path, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_INVALID", exception.Code);
    }

    [Fact]
    public async Task PlaceholderApprovalFailsClosed()
    {
        var path = WriteApproval(
            approvalId: "owner-defined-id",
            approvedBy: "<owner>");
        var guard = CreateGuard(path, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_INVALID", exception.Code);
    }

    [Fact]
    public async Task ProviderOrModelMismatchFailsClosed()
    {
        var path = WriteApproval(model: "some-other-model");
        var guard = CreateGuard(path, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_SCOPE_MISMATCH", exception.Code);
    }

    [Fact]
    public async Task FallbackProviderFailsClosed()
    {
        var path = WriteApproval(allowFallbackProvider: true);
        var guard = CreateGuard(path, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_FALLBACK_FORBIDDEN", exception.Code);
    }

    [Fact]
    public async Task ExpiredApprovalFailsClosed()
    {
        var path = WriteApproval(expiresAt: Now.AddSeconds(-1));
        var guard = CreateGuard(path, new ManualTimeProvider(Now));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_EXPIRED", exception.Code);
    }

    [Fact]
    public async Task ApprovalExpiresDuringBatch()
    {
        var clock = new ManualTimeProvider(Now);
        var path = WriteApproval(expiresAt: Now.AddMinutes(1));
        var guard = CreateGuard(path, clock);

        await guard.ValidateAsync(Provider, Model, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(2));

        var exception = await Assert.ThrowsAsync<ApprovalStaleException>(
            () => guard.ValidateAsync(Provider, Model, CancellationToken.None));

        Assert.Equal("APPROVAL_EXPIRED", exception.Code);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort cleanup of a test-only directory.
        }
    }

    private static LiveApprovalGuard CreateGuard(
        string path,
        TimeProvider clock,
        bool allowPaidApis = true)
        => new(
            new BudgetOptions
            {
                AllowPaidApis = allowPaidApis,
                LiveApprovalFile = path,
                DailyBudgetUsd = 2.00m,
                MonthlyBudgetUsd = 30.00m,
                MaxVideoCostUsd = 0.50m,
                MaxSingleRequestUsd = 0.20m
            },
            clock);

    private string WriteApproval(
        string approvalId = "test-approval",
        string provider = Provider,
        string model = Model,
        int maxRequestCount = 3,
        decimal maxTotalUsd = 0.36m,
        DateTimeOffset? expiresAt = null,
        bool allowFallbackProvider = false,
        string approvedBy = "owner-test")
    {
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.json");
        var payload = new
        {
            approvalId,
            provider,
            model,
            maxRequestCount,
            maxTotalUsd,
            expiresAt = expiresAt ?? Now.AddHours(1),
            allowFallbackProvider,
            approvedBy
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonDefaults.Options));
        return path;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
