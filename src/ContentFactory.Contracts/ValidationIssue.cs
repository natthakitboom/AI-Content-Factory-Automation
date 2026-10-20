namespace ContentFactory.Contracts;

public sealed record ValidationIssue(string Code, string Path, string Message);
