using ContentFactory.Contracts;

namespace ContentFactory.Core.Exceptions;

/// <summary>
/// A script generation attempt stopped in a blocked state. Callers must not enqueue paid
/// voice or video work when this exception is raised.
/// </summary>
public sealed class ScriptPlanningException : Exception
{
    public ScriptPlanningException(
        string code,
        string message,
        int repairRequests = 0,
        IReadOnlyList<ValidationIssue>? validationErrors = null)
        : base(message)
    {
        Code = code;
        RepairRequests = repairRequests;
        ValidationErrors = validationErrors ?? Array.Empty<ValidationIssue>();
    }

    public string Code { get; }

    public bool Blocked => true;

    public int RepairRequests { get; }

    public IReadOnlyList<ValidationIssue> ValidationErrors { get; }
}
