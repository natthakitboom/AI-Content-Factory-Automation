namespace ContentFactory.Contracts;

public sealed record LeasedJob(
    Guid Id, string JobType, string PayloadJson, int PayloadVersion,
    string LeaseOwner, long Fence, DateTimeOffset LeaseUntil);
