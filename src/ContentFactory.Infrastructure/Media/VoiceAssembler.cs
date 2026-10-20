using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;
using ContentFactory.Core.Exceptions;
using ContentFactory.Infrastructure.Providers.Voice;
using ContentFactory.Infrastructure.Storage;

namespace ContentFactory.Infrastructure.Media;

public sealed record VoiceSceneRequest(
    string SceneId,
    string Narration,
    string Caption);

/// <summary>
/// Everything needed to synthesize and time one script revision. Campaign/video scope is
/// required only for a paid provider; uploaded narration needs neither a price nor a
/// reservation.
/// </summary>
public sealed record VoiceAssemblyRequest(
    Guid RevisionId,
    Guid CampaignId,
    Guid VideoScopeId,
    string Locale,
    string VoiceId,
    string Provider,
    string ProviderVersion,
    string VoiceConfigHash,
    string ScriptSha256,
    IReadOnlyList<VoiceSceneRequest> Scenes,
    bool SplitNarrationIntoPhrases = true,
    int MaxPhraseCharacters = 120);

public sealed record VoicePhraseTiming(
    string SceneId,
    int PhraseIndex,
    string Text,
    string Caption,
    AssetRef Audio,
    long StartMs,
    long AudioDurationMs,
    long CaptionEndMs);

public sealed record VoiceManifest(
    int SchemaVersion,
    Guid RevisionId,
    string Locale,
    string VoiceId,
    string Provider,
    string ProviderVersion,
    string ConfigHash,
    string ScriptSha256,
    long TotalDurationMs,
    IReadOnlyList<VoiceSegment> Segments,
    IReadOnlyList<VoicePhraseTiming> Phrases);

public sealed record VoiceAssemblyResult(
    bool Passed,
    IReadOnlyList<ValidationIssue> Issues,
    IReadOnlyList<VoiceSegment> Segments,
    VoiceManifest? Manifest,
    string? ManifestSha256);

public sealed record AudioProbeResult(
    long DurationMs,
    bool HasAudioStream,
    bool IsSilent,
    string? Codec);

public interface IAudioProbe
{
    Task<AudioProbeResult> ProbeAsync(AssetRef audio, CancellationToken ct);
}

public interface IAudioConcatenator
{
    Task<AssetRef> ConcatenateAsync(
        IReadOnlyList<AssetRef> parts,
        CancellationToken ct);
}

public interface IVoiceAssetCache
{
    Task<AssetRef?> TryGetAsync(string key, CancellationToken ct);

    Task PutAsync(string key, AssetRef asset, CancellationToken ct);

    Task RemoveAsync(string key, CancellationToken ct);
}

/// <summary>
/// Cache identity for one synthesized phrase. The key contains the text hash, voice id,
/// locale, synthesis config hash and provider version, so changing any of them produces a
/// new billable operation rather than reusing a stale voice.
/// </summary>
public static class VoiceCacheKey
{
    private const int ExpectedLength = 64;

    public static string Compute(
        string provider,
        string providerVersion,
        string voiceId,
        string locale,
        string configHash,
        string text)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerVersion);
        ArgumentNullException.ThrowIfNull(voiceId);
        ArgumentNullException.ThrowIfNull(locale);
        ArgumentNullException.ThrowIfNull(configHash);
        ArgumentNullException.ThrowIfNull(text);

        var textHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(text)))
            .ToLowerInvariant();
        var canonical = JsonSerializer.Serialize(
            new[]
            {
                "voice-cache-v1",
                provider,
                providerVersion,
                voiceId,
                locale,
                configHash,
                textHash
            },
            JsonDefaults.Options);

        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static bool IsValid(string key)
        => key.Length == ExpectedLength &&
            key.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>
/// Splits narration only at sentence punctuation or whitespace. Thai grapheme clusters are
/// never divided, and a long unbroken word is kept whole rather than truncated. This is
/// phrase segmentation for voice timing, not the caption line-breaking algorithm.
/// </summary>
public static class VoicePhraseSplitter
{
    private static readonly HashSet<string> SentenceEnders =
        new(StringComparer.Ordinal)
        {
            ".", "!", "?", ";", "。", "！", "？", "\n"
        };

    public static IReadOnlyList<string> Split(string text, int maxCharacters)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (maxCharacters < 20)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxCharacters),
                "Phrase segmentation needs at least 20 characters.");
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var phrases = new List<string>();
        var current = new StringBuilder();

        foreach (var element in TextElements(normalized))
        {
            current.Append(element);
            if (SentenceEnders.Contains(element))
            {
                AddIfNotBlank(phrases, current.ToString());
                current.Clear();
            }
        }

        AddIfNotBlank(phrases, current.ToString());

        var result = new List<string>();
        foreach (var phrase in phrases)
        {
            SplitLongPhrase(phrase, maxCharacters, result);
        }

        return result;
    }

    private static void SplitLongPhrase(
        string phrase,
        int maxCharacters,
        ICollection<string> result)
    {
        var elements = TextElements(phrase).ToArray();
        var start = 0;

        while (start < elements.Length)
        {
            var remaining = elements.Length - start;
            if (remaining <= maxCharacters)
            {
                AddElements(result, elements, start, elements.Length);
                return;
            }

            var limit = start + maxCharacters;
            var split = -1;

            for (var index = limit; index > start; index--)
            {
                if (IsWhitespace(elements[index - 1]))
                {
                    split = index - 1;
                    break;
                }
            }

            if (split <= start)
            {
                for (var index = limit; index < elements.Length; index++)
                {
                    if (IsWhitespace(elements[index]))
                    {
                        split = index;
                        break;
                    }
                }
            }

            if (split <= start)
            {
                // There is no safe whitespace boundary. Never cut a word or a grapheme.
                AddElements(result, elements, start, elements.Length);
                return;
            }

            AddElements(result, elements, start, split);
            start = split + 1;
        }
    }

    private static void AddElements(
        ICollection<string> result,
        IReadOnlyList<string> elements,
        int start,
        int end)
    {
        if (end <= start)
        {
            return;
        }

        var phrase = string.Concat(elements.Skip(start).Take(end - start)).Trim();
        if (phrase.Length > 0)
        {
            result.Add(phrase);
        }
    }

    private static void AddIfNotBlank(ICollection<string> result, string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > 0)
        {
            result.Add(trimmed);
        }
    }

    private static bool IsWhitespace(string element)
        => element.Length > 0 && element.All(char.IsWhiteSpace);

    private static IEnumerable<string> TextElements(string value)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            yield return (string)enumerator.Current;
        }
    }
}

/// <summary>
/// Process-local cache for tests and short-lived runs. Production callers should use
/// <see cref="FileVoiceAssetCache"/> so a restart does not synthesize and charge again.
/// </summary>
public sealed class MemoryVoiceAssetCache : IVoiceAssetCache
{
    private readonly ConcurrentDictionary<string, AssetRef> _assets =
        new(StringComparer.Ordinal);

    public Task<AssetRef?> TryGetAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(
            _assets.TryGetValue(key, out var asset) ? asset : null);
    }

    public Task PutAsync(string key, AssetRef asset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ct.ThrowIfCancellationRequested();
        _assets[key] = asset;
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _assets.TryRemove(key, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Owner-only JSON cache under DATA_ROOT/voice-cache. It stores only an immutable asset
/// reference keyed by the phrase cache key; audio bytes remain in the content-addressed
/// asset store. Writes are atomic and never use user text as a path.
/// </summary>
public sealed class FileVoiceAssetCache : IVoiceAssetCache
{
    private const int MaxCacheEntryBytes = 16 * 1024;

    private readonly StorageOptions _storage;

    public FileVoiceAssetCache(StorageOptions storage)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
    }

    public async Task<AssetRef?> TryGetAsync(string key, CancellationToken ct)
    {
        var path = PathFor(key);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaxCacheEntryBytes)
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(path, ct);
            var asset = JsonSerializer.Deserialize<AssetRef>(json, JsonDefaults.Options);
            if (asset is null || !IsSafeAsset(asset))
            {
                return null;
            }

            return asset;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public async Task PutAsync(string key, AssetRef asset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!IsSafeAsset(asset))
        {
            throw new ValidationException(
                "VOICE_CACHE_INVALID",
                "Only a valid audio asset reference may be cached.");
        }

        var path = PathFor(key);
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);

        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var json = JsonSerializer.Serialize(asset, JsonDefaults.Options);
            await File.WriteAllTextAsync(tempPath, json, ct);
            SetOwnerOnly(tempPath);
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    public Task RemoveAsync(string key, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TryDelete(PathFor(key));
        return Task.CompletedTask;
    }

    private string PathFor(string key)
    {
        if (!VoiceCacheKey.IsValid(key))
        {
            throw new ValidationException(
                "VOICE_CACHE_INVALID",
                "A voice cache key must be a lowercase SHA-256 hex string.");
        }

        return Path.Combine(
            _storage.DataRoot,
            "voice-cache",
            key[..2],
            key + ".json");
    }

    private static bool IsSafeAsset(AssetRef asset)
        => asset.Kind == AssetKind.Audio &&
            asset.Bytes > 0 &&
            asset.DurationMs is not <= 0 &&
            !string.IsNullOrWhiteSpace(asset.MimeType) &&
            asset.MimeType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(asset.Sha256) &&
            asset.Sha256.Length == 64 &&
            asset.Sha256.All(character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f') &&
            !string.IsNullOrEmpty(asset.RelativePath) &&
            asset.RelativePath.StartsWith("assets/sha256/", StringComparison.Ordinal) &&
            !asset.RelativePath.Contains("..", StringComparison.Ordinal);

    private static void SetOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; the next run can retry.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; the next run can retry.
        }
    }
}

/// <summary>
/// Synthesizes or resolves one cacheable phrase per caption phrase, measures every audio
/// segment with the injected probe, rejects corrupt/empty/silent narration, concatenates
/// phrases into one scene segment, and records phrase-level timing in a canonical voice
/// manifest. A missing narration is a blocked result and never a silent asset.
/// </summary>
public sealed class VoiceAssembler
{
    private const long MaxNarrationMs = 35_000;
    private const long CaptionPaddingMs = 100;

    private readonly IAudioProbe _probe;
    private readonly IAudioConcatenator _concatenator;
    private readonly IVoiceAssetCache _cache;

    public VoiceAssembler(
        IAssetStore assets,
        IAudioProbe probe,
        IAudioConcatenator concatenator,
        IVoiceAssetCache cache)
    {
        ArgumentNullException.ThrowIfNull(assets);
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _concatenator = concatenator ?? throw new ArgumentNullException(nameof(concatenator));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<VoiceAssemblyResult> AssembleAsync(
        VoiceAssemblyRequest request,
        IVoiceProvider provider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(provider);
        ValidateRequest(request);

        if (request.Scenes is null || request.Scenes.Count == 0)
        {
            throw new ValidationException(
                "VOICE_SCENES_REQUIRED",
                "At least one scene is required to assemble narration.");
        }

        var paid = provider as IPaidVoiceProvider;
        if (paid is not null &&
            (request.CampaignId == Guid.Empty || request.VideoScopeId == Guid.Empty))
        {
            throw new ValidationException(
                "VOICE_BILLING_SCOPE_REQUIRED",
                "Paid narration requires a campaign id and video/revision scope.");
        }

        var issues = new List<ValidationIssue>();
        var seenSceneIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < request.Scenes.Count; index++)
        {
            var scene = request.Scenes[index];
            if (scene is null)
            {
                issues.Add(new ValidationIssue(
                    "VOICE_SCENE_REQUIRED",
                    $"scenes[{index}]",
                    "A scene is required."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(scene.SceneId) || scene.SceneId.Length > 64)
            {
                issues.Add(new ValidationIssue(
                    "VOICE_SCENE_ID_REQUIRED",
                    $"scenes[{index}].sceneId",
                    "A scene id is required."));
            }
            else if (!seenSceneIds.Add(scene.SceneId))
            {
                issues.Add(new ValidationIssue(
                    "VOICE_SCENE_DUPLICATE",
                    $"scenes[{index}].sceneId",
                    "Scene ids must be unique."));
            }

            if (string.IsNullOrWhiteSpace(scene.Narration))
            {
                issues.Add(new ValidationIssue(
                    "VOICE_NARRATION_MISSING",
                    $"scenes[{index}].narration",
                    "Narration is required; no silent asset was created."));
            }

            if (string.IsNullOrWhiteSpace(scene.Caption))
            {
                issues.Add(new ValidationIssue(
                    "VOICE_CAPTION_MISSING",
                    $"scenes[{index}].caption",
                    "A caption is required for phrase timing."));
            }
        }

        if (issues.Count > 0)
        {
            return Failed(issues);
        }

        var segments = new List<VoiceSegment>();
        var phrases = new List<VoicePhraseTiming>();

        foreach (var scene in request.Scenes)
        {
            var phraseTexts = request.SplitNarrationIntoPhrases
                ? VoicePhraseSplitter.Split(scene.Narration, request.MaxPhraseCharacters)
                : [scene.Narration.Trim()];

            if (phraseTexts.Count == 0)
            {
                return Failed(
                [
                    new ValidationIssue(
                        "VOICE_NARRATION_MISSING",
                        $"scenes[{scene.SceneId}].narration",
                        "Narration is required; no silent asset was created.")
                ]);
            }

            var phraseAssets = new List<AssetRef>(phraseTexts.Count);
            var phraseDurations = new List<long>(phraseTexts.Count);

            for (var phraseIndex = 0; phraseIndex < phraseTexts.Count; phraseIndex++)
            {
                var text = phraseTexts[phraseIndex];
                var operationId = request.SplitNarrationIntoPhrases
                    ? VoiceOperationId.ForPhrase(request.RevisionId, scene.SceneId, phraseIndex)
                    : VoiceOperationId.ForScene(request.RevisionId, scene.SceneId);
                var cacheKey = VoiceCacheKey.Compute(
                    request.Provider,
                    request.ProviderVersion,
                    request.VoiceId,
                    request.Locale,
                    request.VoiceConfigHash,
                    text);

                AssetRef measured;
                long measuredDurationMs;
                try
                {
                    var asset = await ResolveOrSynthesizeAsync(
                        request,
                        provider,
                        paid,
                        text,
                        operationId,
                        cacheKey,
                        ct);
                    var probe = await ProbeOrThrowAsync(asset, ct);
                    measured = asset with { DurationMs = probe.DurationMs };
                    measuredDurationMs = probe.DurationMs;
                    await _cache.PutAsync(cacheKey, measured, ct);
                }
                catch (ValidationException exception) when (IsContentFailure(exception.Code))
                {
                    issues.Add(new ValidationIssue(
                        exception.Code,
                        $"scenes[{scene.SceneId}].phrases[{phraseIndex}]",
                        exception.Message));
                    return Failed(issues);
                }

                phraseAssets.Add(measured);
                phraseDurations.Add(measuredDurationMs);
            }

            var combined = await _concatenator.ConcatenateAsync(phraseAssets, ct);
            var combinedProbe = await ProbeOrThrowAsync(combined, ct);
            var measuredCombined = combined with { DurationMs = combinedProbe.DurationMs };
            var segment = new VoiceSegment(
                scene.SceneId,
                measuredCombined,
                combinedProbe.DurationMs);

            if (combinedProbe.DurationMs > MaxNarrationMs)
            {
                issues.Add(new ValidationIssue(
                    "SCRIPT_TOO_LONG",
                    $"scenes[{scene.SceneId}]",
                    "Narration exceeds the 35 second limit; the script must be shortened or re-approved before another paid synthesis."));
                return Failed(issues);
            }

            long startMs = 0;
            for (var phraseIndex = 0; phraseIndex < phraseTexts.Count; phraseIndex++)
            {
                var duration = phraseDurations[phraseIndex];
                var captionEnd = Math.Min(
                    combinedProbe.DurationMs,
                    startMs + duration + CaptionPaddingMs);
                phrases.Add(new VoicePhraseTiming(
                    scene.SceneId,
                    phraseIndex,
                    phraseTexts[phraseIndex],
                    scene.Caption,
                    phraseAssets[phraseIndex],
                    startMs,
                    duration,
                    captionEnd));
                startMs += duration;
            }

            segments.Add(segment);

            if (segments.Sum(item => item.DurationMs) > MaxNarrationMs)
            {
                issues.Add(new ValidationIssue(
                    "SCRIPT_TOO_LONG",
                    "scenes",
                    "Total narration exceeds the 35 second limit."));
                return Failed(issues);
            }
        }

        var manifest = new VoiceManifest(
            SchemaVersion: 1,
            request.RevisionId,
            request.Locale,
            request.VoiceId,
            request.Provider,
            request.ProviderVersion,
            request.VoiceConfigHash,
            request.ScriptSha256,
            segments.Sum(item => item.DurationMs),
            segments,
            phrases);
        var manifestSha256 = ManifestHasher.HashCanonicalJson(
            JsonSerializer.Serialize(manifest, JsonDefaults.Options));

        return new VoiceAssemblyResult(
            Passed: true,
            Issues: [],
            segments,
            manifest,
            manifestSha256);
    }

    private async Task<AssetRef> ResolveOrSynthesizeAsync(
        VoiceAssemblyRequest request,
        IVoiceProvider provider,
        IPaidVoiceProvider? paid,
        string text,
        string operationId,
        string cacheKey,
        CancellationToken ct)
    {
        var cached = await _cache.TryGetAsync(cacheKey, ct);
        if (cached is not null)
        {
            try
            {
                await ProbeOrThrowAsync(cached, ct);
                return cached;
            }
            catch (NotFoundException)
            {
                // The cache entry points at a vanished asset; regenerate it.
                await _cache.RemoveAsync(cacheKey, ct);
            }
        }

        var voiceRequest = new VoiceRequest(
            operationId,
            request.Locale,
            request.VoiceId,
            text,
            request.VoiceConfigHash);

        return paid is null
            ? await provider.SynthesizeAsync(voiceRequest, ct)
            : await paid.SynthesizePaidAsync(
                voiceRequest,
                request.CampaignId,
                request.VideoScopeId,
                ct);
    }

    private async Task<AudioProbeResult> ProbeOrThrowAsync(
        AssetRef asset,
        CancellationToken ct)
    {
        var probe = await _probe.ProbeAsync(asset, ct);
        if (!probe.HasAudioStream)
        {
            throw new ValidationException(
                "VOICE_NARRATION_MISSING_AUDIO",
                "The narration asset has no audio stream.");
        }

        if (probe.DurationMs <= 0)
        {
            throw new ValidationException(
                "VOICE_NARRATION_EMPTY",
                "The narration asset has no measurable duration.");
        }

        if (probe.IsSilent)
        {
            throw new ValidationException(
                "VOICE_NARRATION_SILENT",
                "The narration asset is silent and cannot be used as narration.");
        }

        return probe;
    }

    private static VoiceAssemblyResult Failed(IReadOnlyList<ValidationIssue> issues)
        => new(false, issues, [], null, null);

    private static bool IsContentFailure(string code)
        => code is
            "VOICE_NARRATION_MISSING" or
            "VOICE_NARRATION_INVALID" or
            "VOICE_NARRATION_DUPLICATE" or
            "VOICE_ASSET_INVALID" or
            "VOICE_TEXT_MISMATCH" or
            "VOICE_TEXT_INVALID" or
            "VOICE_LOCALE_MISMATCH" or
            "VOICE_LOCALE_INVALID" or
            "VOICE_VOICE_ID_INVALID" or
            "VOICE_NOT_ALLOWED" or
            "VOICE_NARRATION_MISSING_AUDIO" or
            "VOICE_NARRATION_EMPTY" or
            "VOICE_NARRATION_CORRUPT" or
            "VOICE_NARRATION_SILENT";

    private static void ValidateRequest(VoiceAssemblyRequest request)
    {
        if (request.RevisionId == Guid.Empty)
        {
            throw new ValidationException(
                "VOICE_REVISION_REQUIRED",
                "A non-empty revision id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Locale) || request.Locale.Length > 35)
        {
            throw new ValidationException(
                "VOICE_LOCALE_INVALID",
                "The narration locale is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.VoiceId) || request.VoiceId.Length > 256)
        {
            throw new ValidationException(
                "VOICE_VOICE_ID_INVALID",
                "The selected voice id is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.Provider) || request.Provider.Length > 32)
        {
            throw new ValidationException(
                "VOICE_PROVIDER_INVALID",
                "The voice provider name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.ProviderVersion) ||
            request.ProviderVersion.Length > 64)
        {
            throw new ValidationException(
                "VOICE_PROVIDER_INVALID",
                "The voice provider version is invalid.");
        }

        if (string.IsNullOrWhiteSpace(request.VoiceConfigHash) ||
            request.VoiceConfigHash.Length > 64)
        {
            throw new ValidationException(
                "VOICE_CONFIG_INVALID",
                "A speech config hash between 1 and 64 characters is required.");
        }

        if (string.IsNullOrWhiteSpace(request.ScriptSha256) ||
            request.ScriptSha256.Length > 128)
        {
            throw new ValidationException(
                "VOICE_SCRIPT_HASH_INVALID",
                "The script hash is invalid.");
        }

        if (request.MaxPhraseCharacters < 20 || request.MaxPhraseCharacters > 4000)
        {
            throw new ValidationException(
                "VOICE_PHRASE_LIMIT_INVALID",
                "The phrase character limit must be between 20 and 4000.");
        }
    }
}
