namespace ContentFactory.Contracts;

public sealed record ValidationReport(
    bool Passed, IReadOnlyList<ValidationIssue> Errors,
    IReadOnlyList<ValidationIssue> Warnings);
