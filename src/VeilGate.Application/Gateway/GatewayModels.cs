using VeilGate.Application.Llm;
using VeilGate.Domain.Common;
using VeilGate.Domain.Detection;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Gateway;

/// <summary>Who is calling, taken from the validated token and never from the request body. CanReidentify = the token has the gateway:reidentify scope.</summary>
public sealed record GatewayCaller(Guid TenantId, Guid ClientId, bool CanReidentify, string? Ip = null);

/// <summary>One protected value: where it was in the input, what was done, and the token if it was tokenized. Never the value itself.</summary>
public sealed record ProtectedEntity(string Type, int Start, int End, PolicyAction Action, string? Token);

public sealed record DetectOutput(string RequestId, IReadOnlyList<DetectedEntity> Entities);

public sealed record DeidentifyOutput(string RequestId, string Text, IReadOnlyList<ProtectedEntity> Entities, Guid VaultSessionId, DateTimeOffset ExpiresAt);

public sealed record ReidentifyOutput(string Text, int Replaced, IReadOnlyList<string> Unresolved);

public sealed record ChatInput(string Model, IReadOnlyList<LlmMessage> Messages, double? Temperature = null, int? MaxTokens = null);

public sealed record ChatOutput(
    string RequestId, LlmResponse Response, string Content, bool Restored, int PolicyVersion, IReadOnlyDictionary<string, int> EntitiesProtected);

/// <summary>
/// The four things the gateway does (Backend doc 4.1). Every operation is fail-closed: nothing reaches a language model unless
/// detection, the policy decision, the transformation and the leak gate all completed, and any exception stops the call.
/// </summary>
public interface IGatewayPipeline
{
    /// <summary>Finds entities and reports positions only. Offsets refer to the text after normalization (Unicode NFKC, zero-width characters removed).</summary>
    Task<Result<DetectOutput>> DetectAsync(GatewayCaller caller, string text, double minConfidence, CancellationToken ct = default);

    Task<Result<DeidentifyOutput>> DeidentifyAsync(GatewayCaller caller, string text, Guid? policyId, string? subjectId, TimeSpan? vaultTtl, CancellationToken ct = default);

    Task<Result<ReidentifyOutput>> ReidentifyAsync(GatewayCaller caller, Guid vaultSessionId, string text, CancellationToken ct = default);

    Task<Result<ChatOutput>> ChatAsync(GatewayCaller caller, ChatInput input, Guid? policyId, CancellationToken ct = default);
}
