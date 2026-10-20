namespace ContentFactory.Contracts;

public sealed record VoiceRequest(
    string OperationId, string Locale, string VoiceId,
    string Text, string VoiceConfigHash);
