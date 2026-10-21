namespace ContentFactory.Infrastructure.Persistence.Entities;

/// <summary>A small durable switch shared by the API, worker and CLI (for example write quiesce).</summary>
public sealed class SystemFlag
{
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}
