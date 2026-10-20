namespace ContentFactory.Infrastructure.Persistence.Entities;

public sealed class BudgetPeriod
{
    public Guid Id { get; set; }
    public string ScopeType { get; set; } = string.Empty;
    public Guid ScopeId { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public DateTimeOffset PeriodEnd { get; set; }
    public decimal LimitUsd { get; set; }
    public decimal ReservedUsd { get; set; }
    public decimal SpentUsd { get; set; }
}
