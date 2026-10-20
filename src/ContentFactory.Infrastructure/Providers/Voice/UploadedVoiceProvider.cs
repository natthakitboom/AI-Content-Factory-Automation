using System.Security.Cryptography;
using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;

namespace ContentFactory.Infrastructure.Providers.Voice;

/// <summary>
/// An owner-supplied narration file for one scene phrase. Uploaded narration is already
/// licensed and already exists in the content-addressed asset store, so it never needs a
/// price snapshot or a budget reservation.
/// </summary>
public sealed record UploadedNarrationAsset(
    string SceneId,
    string Locale,
    AssetRef Audio,
    string? TextSha256 = null,
    int PhraseIndex = 0);

/// <summary>
/// Deterministic operation IDs shared by the assembler, uploaded narration mapping and the
/// paid budget guard. Scene IDs are already validated by the script contract and are never
/// turned into paths or shell arguments.
/// </summary>
public static class VoiceOperationId
{
    private const int MaxOperationIdLength = 256;

    public static string ForScene(Guid revisionId, string sceneId)
    {
        Validate(revisionId, sceneId, phraseIndex: null);
        return $"voice:{revisionId:N}:{sceneId}";
    }

    public static string ForPhrase(Guid revisionId, string sceneId, int phraseIndex)
    {
        if (phraseIndex < 0)
        {
            throw new ValidationException(
                "VOICE_OPERATION_INVALID",
                "A phrase index must be zero or greater.");
        }

        Validate(revisionId, sceneId, phraseIndex);
        return $"voice:{revisionId:N}:{sceneId}:p{phraseIndex}";
    }

    public static string HashText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
    }

    private static void Validate(Guid revisionId, string sceneId, int? phraseIndex)
    {
        if (revisionId == Guid.Empty)
        {
            throw new ValidationException(
                "VOICE_OPERATION_INVALID",
                "A non-empty revision id is required for a voice operation.");
        }

        if (string.IsNullOrWhiteSpace(sceneId) ||
            sceneId.Length > 64 ||
            sceneId.Contains('\0') ||
            sceneId.Any(char.IsControl))
        {
            throw new ValidationException(
                "VOICE_OPERATION_INVALID",
                "The scene id is not valid for a voice operation.");
        }

        var length = $"voice:{revisionId:N}:{sceneId}".Length +
            (phraseIndex is { } index ? $":p{index}".Length : 0);
        if (length > MaxOperationIdLength)
        {
            throw new ValidationException(
                "VOICE_OPERATION_INVALID",
                "The voice operation id exceeds its configured limit.");
        }
    }
}

/// <summary>
/// Resolves narration that the owner uploaded. A missing mapping is a blocked input, never
/// an empty or silent replacement asset. The provider intentionally does not copy or
/// re-encode the uploaded file: the immutable asset reference is returned as-is.
/// </summary>
public sealed class UploadedVoiceProvider : IVoiceProvider
{
    private readonly IReadOnlyDictionary<string, UploadedNarrationAsset> _byOperation;

    private UploadedVoiceProvider(
        IEnumerable<UploadedNarrationAsset> assets,
        bool phraseIndexed,
        Guid? revisionId)
    {
        ArgumentNullException.ThrowIfNull(assets);

        var map = new Dictionary<string, UploadedNarrationAsset>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            if (asset is null)
            {
                throw new ValidationException(
                    "VOICE_NARRATION_INVALID",
                    "An uploaded narration entry cannot be null.");
            }

            ValidateEntry(asset);
            if (revisionId is not { } revision)
            {
                throw new ValidationException(
                    "VOICE_OPERATION_INVALID",
                    "A revision id is required to map uploaded narration to operations.");
            }

            var operationId = phraseIndexed
                ? VoiceOperationId.ForPhrase(revision, asset.SceneId, asset.PhraseIndex)
                : VoiceOperationId.ForScene(revision, asset.SceneId);

            if (!map.TryAdd(operationId, asset))
            {
                throw new ValidationException(
                    "VOICE_NARRATION_DUPLICATE",
                    "Only one uploaded narration file may target the same scene phrase.");
            }
        }

        _byOperation = map;
    }

    public static UploadedVoiceProvider ForScenes(
        Guid revisionId,
        IEnumerable<UploadedNarrationAsset> assets)
        => new(assets, phraseIndexed: false, revisionId);

    public static UploadedVoiceProvider ForPhrases(
        Guid revisionId,
        IEnumerable<UploadedNarrationAsset> assets)
        => new(assets, phraseIndexed: true, revisionId);

    public Task<AssetRef> SynthesizeAsync(
        VoiceRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(request.OperationId))
        {
            throw new ValidationException(
                "VOICE_OPERATION_REQUIRED",
                "An operation id is required to resolve uploaded narration.");
        }

        if (!_byOperation.TryGetValue(request.OperationId, out var entry))
        {
            throw new ValidationException(
                "VOICE_NARRATION_MISSING",
                "No uploaded narration file is mapped to this scene phrase.");
        }

        if (!string.Equals(entry.Locale, request.Locale, StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                "VOICE_LOCALE_MISMATCH",
                "The uploaded narration locale does not match the requested locale.");
        }

        if (entry.TextSha256 is { Length: > 0 } expected &&
            !string.Equals(
                expected,
                VoiceOperationId.HashText(request.Text),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException(
                "VOICE_TEXT_MISMATCH",
                "The uploaded narration was not approved for this narration text.");
        }

        ValidateAudio(entry.Audio);
        return Task.FromResult(entry.Audio);
    }

    private static void ValidateEntry(UploadedNarrationAsset asset)
    {
        if (string.IsNullOrWhiteSpace(asset.SceneId) || asset.SceneId.Length > 64)
        {
            throw new ValidationException(
                "VOICE_NARRATION_INVALID",
                "An uploaded narration entry requires a valid scene id.");
        }

        if (string.IsNullOrWhiteSpace(asset.Locale) || asset.Locale.Length > 35)
        {
            throw new ValidationException(
                "VOICE_NARRATION_INVALID",
                "An uploaded narration entry requires a valid locale.");
        }

        ValidateAudio(asset.Audio);
    }

    private static void ValidateAudio(AssetRef audio)
    {
        if (audio.Kind != AssetKind.Audio ||
            audio.Bytes <= 0 ||
            string.IsNullOrWhiteSpace(audio.MimeType) ||
            !audio.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
            audio.DurationMs is <= 0)
        {
            throw new ValidationException(
                "VOICE_ASSET_INVALID",
                "The uploaded narration must be a non-empty audio asset with a positive duration.");
        }

        if (string.IsNullOrEmpty(audio.Sha256) || audio.Sha256.Length != 64 ||
            !audio.Sha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f'))
        {
            throw new ValidationException(
                "VOICE_ASSET_INVALID",
                "The uploaded narration asset hash is invalid.");
        }
    }
}
