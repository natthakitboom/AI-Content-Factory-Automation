namespace ContentFactory.Core.Exceptions;

/// <summary>
/// A reservation would exceed a configured or persisted spending cap. No budget row or
/// provider request may be written after this exception.
/// </summary>
public sealed class BudgetExceededException : Exception
{
    public BudgetExceededException(string scopeType, string message)
        : base(message)
    {
        ScopeType = scopeType;
    }

    public string Code => "BUDGET_EXCEEDED";

    public string ScopeType { get; }
}
