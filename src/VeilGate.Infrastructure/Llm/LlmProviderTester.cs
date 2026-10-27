using System.Diagnostics;
using VeilGate.Application.Llm;

namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Sends "ping" with a one-token limit to the provider's first model, through the same client, timeouts and circuit breaker real
/// calls use. The text is a fixed word, never anyone's data. The answer is a category, never what the provider said.
/// </summary>
public sealed class LlmProviderTester(ProviderCaller caller) : ILlmProviderTester
{
    public async Task<ProviderTestResult> TestAsync(LlmProviderConfig provider, CancellationToken ct = default)
    {
        var request = new LlmRequest(provider.Models[0], [new LlmMessage("user", "ping")], Temperature: 0, MaxTokens: 1);

        var clock = Stopwatch.StartNew();
        var result = await caller.CallAsync(provider, request, ct);
        clock.Stop();

        return result.Response is not null
            ? new ProviderTestResult(true, clock.ElapsedMilliseconds, null)
            : new ProviderTestResult(false, clock.ElapsedMilliseconds, Category(result.Failure));
    }

    private static string Category(ProviderFailure failure) => failure switch
    {
        ProviderFailure.Timeout => "timeout",
        ProviderFailure.Unreachable => "unreachable",
        ProviderFailure.CircuitOpen => "circuit_open",
        ProviderFailure.Overloaded => "overloaded",
        ProviderFailure.BadResponse => "invalid_response",
        _ => "http_error"
    };
}
