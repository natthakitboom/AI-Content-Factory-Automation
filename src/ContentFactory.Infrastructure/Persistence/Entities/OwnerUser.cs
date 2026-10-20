namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class OwnerUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public bool Active { get; set; } = true;
    public int SessionVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
