using VeilGate.Application.Llm;
using VeilGate.Domain.Common;

namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Sends a sanitized chat request to the first healthy provider of the current tenant that serves the model (Backend doc 6.5).
/// The tenant's enabled providers come from the catalog in failover order. Each provider has its own client and resilience pipeline
/// (timeouts, retries, circuit breaker), so one sick provider cannot trip the breaker of another, and one tenant's providers
/// are never even considered for another tenant's request.
/// </summary>
public sealed class LlmGateway(ILlmProviderCatalog catalog, ProviderCaller caller) : ILlmGateway
{
    public async Task<Result<LlmResponse>> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var candidates = await catalog.GetForModelAsync(request.Model, ct);
        if (candidates.Count == 0) return Error.ProviderUnavailable();

        var lastFailureWasTimeout = false;
        foreach (var provider in candidates)
        {
            var result = await caller.CallAsync(provider, request, ct);
            if (result.Response is not null) return result.Response;
            lastFailureWasTimeout = result.Failure == ProviderFailure.Timeout;   // any other failure: try the next provider
        }

        return lastFailureWasTimeout ? Error.ProviderTimeout() : Error.ProviderUnavailable();
    }
}
