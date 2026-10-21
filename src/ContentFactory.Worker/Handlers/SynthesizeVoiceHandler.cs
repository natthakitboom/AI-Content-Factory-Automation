using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Media;
using ContentFactory.Infrastructure.Persistence;
using ContentFactory.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ContentFactory.Worker.Handlers;

public sealed record VoiceHandlerOptions(
    string Provider,
    string VoiceId,
    string ProviderVersion,
    string VoiceConfigHash);

/// <summary>
/// Builds one narration asset per scene through <see cref="VoiceAssembler"/> and records
/// them as scene assets. Missing or silent audio fails the stage; uploaded narration is
/// not wired to this handler yet, so a run that did not ask for generated voice blocks.
/// </summary>
public sealed class SynthesizeVoiceHandler : IJobHandler
{
    public const string JobTypeName = "synthesize_voice";
    public const string VoiceRole = "voice";

    private readonly AppDbContext _db;
    private readonly VoiceAssembler _assembler;
    private readonly IVoiceProvider _provider;
    private readonly VoiceHandlerOptions _options;

    public SynthesizeVoiceHandler(
        AppDbContext db,
        VoiceAssembler assembler,
        IVoiceProvider provider,
        VoiceHandlerOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _assembler = assembler ?? throw new ArgumentNullException(nameof(assembler));
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public string JobType => JobTypeName;

    public async Task<JobOutcome> ExecuteAsync(LeasedJob job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ct.ThrowIfCancellationRequested();

        var payload = RunPayload.TryRead(job.PayloadJson);
        if (payload is null)
        {
            return Blocked("VOICE_PAYLOAD_INVALID");
        }

        if (!payload.AutoGenerateVoice)
        {
            return Blocked("VOICE_UPLOAD_NOT_WIRED");
        }

        var revision = await _db.ContentRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == payload.RevisionId, ct);
        if (revision?.ScriptJson is null || revision.ScriptHash is null)
        {
            return Blocked("SCRIPT_MISSING");
        }

        var script = JsonSerializer.Deserialize<ScriptDocument>(
            revision.ScriptJson, JsonDefaults.Options)!;
        var request = new VoiceAssemblyRequest(
            revision.Id,
            revision.CampaignId,
            revision.Id,
            script.Locale,
            _options.VoiceId,
            _options.Provider,
            _options.ProviderVersion,
            _options.VoiceConfigHash,
            revision.ScriptHash,
            script.Scenes
                .Select(scene => new VoiceSceneRequest(scene.SceneId, scene.Narration, scene.Caption))
                .ToArray());

        VoiceAssemblyResult result;
        try
        {
            result = await _assembler.AssembleAsync(request, _provider, ct);
        }
        catch (ValidationException exception)
        {
            return Blocked(exception.Code);
        }

        if (!result.Passed)
        {
            return Blocked(result.Issues.FirstOrDefault()?.Code ?? "VOICE_FAILED");
        }

        var existing = (await _db.SceneAssets
                .Where(x => x.ContentRevisionId == revision.Id && x.Role == VoiceRole)
                .ToListAsync(ct))
            .Select(x => x.SceneId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var segment in result.Segments)
        {
            // Duration is derived, measured metadata; the blob itself stays immutable.
            var asset = await _db.Assets.SingleAsync(x => x.Id == segment.Audio.Id, ct);
            asset.DurationMs ??= segment.DurationMs;
            if (existing.Add(segment.SceneId))
            {
                _db.SceneAssets.Add(new SceneAsset
                {
                    Id = Guid.NewGuid(),
                    ContentRevisionId = revision.Id,
                    SceneId = segment.SceneId,
                    Role = VoiceRole,
                    AssetId = segment.Audio.Id
                });
            }
        }

        await _db.SaveChangesAsync(ct);
        return new JobOutcome("succeeded", null, null, revision.Id.ToString("D"));
    }

    private static JobOutcome Blocked(string code) => new("blocked", null, code, null);
}
