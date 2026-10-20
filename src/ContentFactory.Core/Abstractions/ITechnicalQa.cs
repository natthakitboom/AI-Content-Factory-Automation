using ContentFactory.Contracts;

namespace ContentFactory.Core.Abstractions;

public interface ITechnicalQa
{
    Task<ValidationReport> CheckAsync(RenderResult result, CancellationToken ct);
}
