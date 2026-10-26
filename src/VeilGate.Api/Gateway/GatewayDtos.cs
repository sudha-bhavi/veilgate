using System.Text.Json.Serialization;

namespace VeilGate.Api.Gateway;

// The JSON bodies of the gateway endpoints (Backend doc 4.1). They contain only what the caller may choose: never a tenant id,
// a client id or a role. Those come from the verified token, and an unknown property is rejected outright.

public sealed record DetectOptionsDto(double? MinConfidence);

public sealed record DetectRequest(string Text, DetectOptionsDto? Options);

public sealed record DeidentifyRequest(string Text, Guid? PolicyId, string? SubjectId, int? VaultTtlSeconds);

public sealed record ReidentifyRequest(Guid VaultSessionId, string Text);

public sealed record ChatMessageDto(string Role, string Content);

/// <summary>The OpenAI chat shape, so existing clients only change their base URL.</summary>
public sealed record ChatCompletionRequest(
    string Model,
    List<ChatMessageDto> Messages,
    bool? Stream,
    double? Temperature,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens);

public sealed record PlaygroundRequest(string Mode, string Text, string? Model, Guid? PolicyId);
