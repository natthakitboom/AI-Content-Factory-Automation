namespace ContentFactory.Api.Middleware;

public static class ApiProblem
{
    public static IResult Create(int statusCode, string code, string title, string? detail = null)
        => Results.Problem(
            statusCode: statusCode,
            title: title,
            detail: detail,
            extensions: new Dictionary<string, object?> { ["code"] = code });
}
