namespace ContentFactory.Contracts;

public sealed record ProviderSubmission(
    string Provider, string Model, string RequestId,
    Uri StatusUrl, Uri ResponseUrl, Uri? CancelUrl);
