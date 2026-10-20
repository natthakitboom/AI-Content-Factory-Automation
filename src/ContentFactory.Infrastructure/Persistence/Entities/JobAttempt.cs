namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class JobAttempt
{
    public Guid Id { get; set; }
    public Guid JobId { get; set; }
    public int AttemptNo { get; set; }
    public long Fence { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public string? ErrorCode { get; set; }
    public string? TraceId { get; set; }
}
