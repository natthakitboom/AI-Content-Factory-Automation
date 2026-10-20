using ContentFactory.Contracts;

namespace ContentFactory.Infrastructure.Billing;

/// <summary>
/// Supplies a verified, unexpired price quote for a speech operation. A missing snapshot
/// is a hard block: no TTS request may be submitted from a guessed price.
/// </summary>
public interface IVoicePriceQuoter
{
    Task<PriceQuote> GetQuoteAsync(
        string provider,
        string model,
        string configHash,
        CancellationToken ct);
}
