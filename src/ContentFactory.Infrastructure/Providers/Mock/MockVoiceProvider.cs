using System.Text;
using ContentFactory.Contracts;
using ContentFactory.Core.Abstractions;

namespace ContentFactory.Infrastructure.Providers.Mock;

/// <summary>
/// Offline narration stand-in: an audible sine tone whose length follows the text. It is
/// not speech and is never presented as real narration; the pipeline's silence check still
/// applies, so a silent file cannot pass as success.
/// </summary>
public sealed class MockVoiceProvider : IVoiceProvider
{
    private const int SampleRate = 24_000;
    private const int MinMs = 5_200;
    private const int MaxMs = 8_000;
    private const int MsPerCharacter = 90;

    private readonly IAssetStore _assets;

    public MockVoiceProvider(IAssetStore assets)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
    }

    public async Task<AssetRef> SynthesizeAsync(VoiceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();

        var durationMs = Math.Clamp(request.Text.Length * MsPerCharacter, MinMs, MaxMs);
        await using var stream = new MemoryStream(BuildWav(durationMs));
        return await _assets.PutAsync(stream, AssetKind.Audio, "audio/wav", ct);
    }

    private static byte[] BuildWav(int durationMs)
    {
        var samples = SampleRate * durationMs / 1000;
        var dataBytes = samples * 2;
        using var memory = new MemoryStream(44 + dataBytes);
        using var writer = new BinaryWriter(memory, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        for (var index = 0; index < samples; index++)
        {
            var value = Math.Sin(2 * Math.PI * 330 * index / SampleRate) * 0.25 * short.MaxValue;
            writer.Write((short)value);
        }

        return memory.ToArray();
    }
}
