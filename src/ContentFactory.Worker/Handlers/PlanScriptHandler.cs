using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

/// <summary>
/// Generates the script through the configured planner and stores it only after the
/// deterministic validator passes. A planner never decides the stage graph or approves
/// claims; an invalid script blocks the revision instead of continuing.
/// </summary>
public sealed class PlanScriptHandler : IJobHandler
{
    public const string JobTypeName = "plan_script";

    private readonly AppDbContext _db;
    private readonly IScriptPlanner _planner;
    private readonly IScriptValidator _validator;
    private readonly TimeProvider _clock;

    public PlanScriptHandler(
        AppDbContext db,
        IScriptPlanner planner,
        IScriptValidator validator,
        TimeProvider clock)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var payload = RunPayload.TryRead(job.PayloadJson);
        if (payload is null)
        {
            return Blocked("PLAN_PAYLOAD_INVALID");
        }

        var revision = await _db.ContentRevisions
            .SingleOrDefaultAsync(x => x.Id == payload.RevisionId, ct);
        if (revision is null)
        {
            return Blocked("REVISION_NOT_FOUND");
        }

        if (revision.Status == "validated" && revision.ScriptJson is not null)
        {
            return new JobOutcome("succeeded", null, null, revision.Id.ToString("D"));
        }

        ScriptDocument script;
        try
        {
            script = await _planner.GenerateAsync(payload.Request, ct);
        }
        catch (ScriptPlanningException exception)
        {
            revision.Status = "blocked";
            await _db.SaveChangesAsync(ct);
            return Blocked(exception.Code);
        }

        var report = _validator.Validate(script, payload.Request.Product);
        if (!report.Passed)
        {
            revision.Status = "blocked";
            await _db.SaveChangesAsync(ct);
            return Blocked(report.Errors.FirstOrDefault()?.Code ?? "SCRIPT_INVALID");
        }

        var scriptJson = JsonSerializer.Serialize(script, JsonDefaults.Options);
        revision.ScriptJson = scriptJson;
        revision.ScriptHash = ManifestHasher.HashCanonicalJson(scriptJson);
        revision.ModelId = _planner is ContentFactory.Infrastructure.Providers.Mock.MockScriptPlanner
            ? "mock"
            : null;
        revision.Status = "validated";
        await _db.SaveChangesAsync(ct);
        _ = _clock;
        return new JobOutcome("succeeded", null, null, revision.Id.ToString("D"));
    }

    private static JobOutcome Blocked(string code) => new("blocked", null, code, null);
}
