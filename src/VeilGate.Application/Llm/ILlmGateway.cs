using VeilGate.Domain.Common;

namespace VeilGate.Application.Llm;

public sealed record LlmMessage(string Role, string Content);

/// <summary>An OpenAI-style chat request. By the time this is built, the text has already been de-identified.</summary>
public sealed record LlmRequest(string Model, IReadOnlyList<LlmMessage> Messages, double? Temperature = null, int? MaxTokens = null);

public sealed record LlmResponse(string Id, string Model, string Content, string FinishReason, int PromptTokens, int CompletionTokens, string Provider);

/// <summary>Sends sanitized text to a language model provider. Failures are results with stable codes: provider.unavailable, provider.timeout.</summary>
public interface ILlmGateway
{
    Task<Result<LlmResponse>> CompleteAsync(LlmRequest request, CancellationToken ct = default);
}
