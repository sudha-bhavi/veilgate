using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using VeilGate.Application.Llm;
using VeilGate.Domain.Common;

namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Sends a sanitized chat request to the first healthy provider that serves the model (Backend doc 6.5).
/// Each provider has its own HTTP client with its own resilience pipeline (timeouts, retries, circuit breaker),
/// so one sick provider cannot trip the breaker of another. Nothing in this class logs request or response text.
/// </summary>
public sealed class LlmGateway(IHttpClientFactory clients, IOptions<LlmOptions> options, ILogger<LlmGateway> logger) : ILlmGateway
{
    public static string ClientName(string provider) => $"llm:{provider}";

    public async Task<Result<LlmResponse>> CompleteAsync(LlmRequest request, CancellationToken ct = default)
    {
        var candidates = options.Value.Providers
            .Where(p => p.Models.Count == 0 || p.Models.Contains(request.Model, StringComparer.Ordinal))
            .OrderBy(p => p.Priority)
            .ToList();
        if (candidates.Count == 0) return Error.ProviderUnavailable();

        var lastFailureWasTimeout = false;
        foreach (var provider in candidates)
        {
            try
            {
                var response = await CallAsync(provider, request, ct);
                if (response is not null) return response;
                lastFailureWasTimeout = false;   // it answered, but with an error or nonsense: try the next provider
            }
            catch (Exception ex) when (IsTimeout(ex, ct))
            {
                logger.LogWarning("LLM provider {Provider} timed out", provider.Name);
                lastFailureWasTimeout = true;
            }
            catch (Exception ex) when (ex is HttpRequestException or BrokenCircuitException or RateLimiterRejectedException)
            {
                logger.LogWarning("LLM provider {Provider} failed: {Kind}", provider.Name, ex.GetType().Name);
                lastFailureWasTimeout = false;
            }
        }

        return lastFailureWasTimeout ? Error.ProviderTimeout() : Error.ProviderUnavailable();
    }

    // Our own cancellation (the caller went away) is not a timeout and must not be swallowed.
    private static bool IsTimeout(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && (ex is TimeoutRejectedException or TaskCanceledException);

    private async Task<LlmResponse?> CallAsync(LlmProviderOptions provider, LlmRequest request, CancellationToken ct)
    {
        var client = clients.CreateClient(ClientName(provider.Name));
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions")
        {
            Content = JsonContent.Create(BuildBody(request)),
        };
        if (!string.IsNullOrEmpty(provider.ApiKey))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);

        using var response = await client.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("LLM provider {Provider} answered {Status}", provider.Name, (int)response.StatusCode);
            return null;
        }
        return await ParseAsync(response, provider.Name, request.Model, ct);
    }

    private static Dictionary<string, object?> BuildBody(LlmRequest r)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = r.Model,
            ["messages"] = r.Messages.Select(m => new { role = m.Role, content = m.Content }).ToList(),
            ["stream"] = false,
        };
        if (r.Temperature is not null) body["temperature"] = r.Temperature;
        if (r.MaxTokens is not null) body["max_tokens"] = r.MaxTokens;
        return body;
    }

    private async Task<LlmResponse?> ParseAsync(HttpResponseMessage response, string provider, string model, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var choice = root.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (content is null) return null;

            var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() ?? "stop" : "stop";
            var prompt = 0;
            var completion = 0;
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pt)) prompt = pt;
                if (usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ct2)) completion = ct2;
            }

            var id = root.TryGetProperty("id", out var i) ? i.GetString() ?? string.Empty : string.Empty;
            var answeredModel = root.TryGetProperty("model", out var m) ? m.GetString() ?? model : model;
            return new LlmResponse(id, answeredModel, content, finish, prompt, completion, provider);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            logger.LogWarning("LLM provider {Provider} sent a response we could not read", provider);
            return null;
        }
    }
}
