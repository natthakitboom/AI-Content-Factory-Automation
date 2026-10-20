namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class Schedule
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string Timezone { get; set; } = "Asia/Bangkok";
    public TimeOnly LocalTime { get; set; }
    public int[] DaysOfWeek { get; set; } = [];
    public bool Enabled { get; set; }
    public int MaxJobsPerDay { get; set; } = 1;
    public DateTimeOffset? LastSlot { get; set; }
    public long Version { get; set; } = 1;
}
