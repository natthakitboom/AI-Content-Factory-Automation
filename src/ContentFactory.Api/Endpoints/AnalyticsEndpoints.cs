using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Analytics;

namespace ContentFactory.Api.Endpoints;

/// <summary>
/// CSV import with an explicit column mapping and a per-source overview. Definitions are
/// returned with the numbers so a reader sees what each figure does and does not mean.
/// </summary>
public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/metrics/imports", ImportAsync).RequireAuthorization().DisableAntiforgery();
        endpoints.MapGet("/api/v1/analytics/overview", OverviewAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ImportAsync(
        HttpRequest request, MetricsImporter importer, CancellationToken ct)
    {
        if (!request.HasFormContentType)
        {
            throw new ValidationException("METRICS_FORM_REQUIRED", "Send multipart form data with a file and a mapping.");
        }

        var form = await request.ReadFormAsync(ct);
        var file = form.Files.GetFile("file")
            ?? throw new ValidationException("METRICS_FILE_REQUIRED", "A CSV file is required.");
        ImportMapping? mapping;
        try
        {
            mapping = JsonSerializer.Deserialize<ImportMapping>(form["mapping"].ToString(), JsonDefaults.Options);
        }
        catch (JsonException)
        {
            mapping = null;
        }

        if (mapping is null)
        {
            throw new ValidationException("METRICS_MAPPING_INVALID", "The mapping field must be valid JSON.");
        }

        await using var stream = file.OpenReadStream();
        var report = await importer.ImportAsync(stream, mapping, "owner", ct);
        return Results.Accepted(value: report);
    }

    private static async Task<IResult> OverviewAsync(MetricsImporter importer, CancellationToken ct)
    {
        var sources = await importer.OverviewBySourceAsync(ct);
        return Results.Ok(new
        {
            bySource = sources,
            hasData = sources.Count > 0,
            note = "Sources can overlap, so they are never added together. A blank value means no data, not zero.",
            definitions = new
            {
                ctr = "product clicks / views, over posts that reported both; blank when either is missing or views is 0",
                cvr = "orders / product clicks, over posts that reported both; blank when clicks is missing or 0. Not proof that a video caused an order.",
                commission = "Per currency. Settled commission is net of returned commission. Pending is not income.",
                snapshots = "Each figure is the latest cumulative snapshot per post, never a sum of snapshots."
            }
        });
    }
}
