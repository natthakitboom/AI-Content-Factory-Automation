namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class ProviderRequest
{
    public Guid Id { get; set; }
    public Guid? JobId { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public string ClientOperationId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string? ProviderRequestId { get; set; }
    public string? StatusUrl { get; set; }
    public string? ResponseUrl { get; set; }
    public string? CancelUrl { get; set; }
    public string Status { get; set; } = "prepared";
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
