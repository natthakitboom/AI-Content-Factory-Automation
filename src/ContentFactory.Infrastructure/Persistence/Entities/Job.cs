using ContentFactory.Contracts;

namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Job
{
    public Guid Id { get; set; }
    public string JobType { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public int PayloadVersion { get; set; } = 1;
    public string DedupeKey { get; set; } = string.Empty;
    public JobLane Lane { get; set; } = JobLane.Control;
    public string Status { get; set; } = "queued";
    public int Priority { get; set; } = 100;
    public DateTimeOffset RunAfter { get; set; }
    public int Attempts { get; set; }
    public int MaxAttempts { get; set; } = 5;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
    public long Fence { get; set; }
    public string? LastErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
