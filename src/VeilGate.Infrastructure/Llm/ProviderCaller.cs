using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.RateLimiting;
using Polly.Timeout;
using VeilGate.Application.Llm;

namespace VeilGate.Infrastructure.Llm;

/// <summary>Why a call to a provider did not give an answer. Only these fixed words ever leave this class, never the provider's own text.</summary>
public enum ProviderFailure { None, Timeout, Unreachable, CircuitOpen, Overloaded, HttpError, BadResponse }

public sealed record ProviderCallResult(LlmResponse? Response, ProviderFailure Failure);

/// <summary>
/// Makes one call to one provider through its resilient client and turns whatever happens into a <see cref="ProviderCallResult"/>.
/// Shared by real traffic (failover across providers) and by the admin "test this provider" button.
/// Nothing here logs request or response text.
/// </summary>
public sealed class ProviderCaller(ProviderClientCache clients, ILogger<ProviderCaller> logger)
{
    public async Task<ProviderCallResult> CallAsync(LlmProviderConfig provider, LlmRequest request, CancellationToken ct)
    {
        var client = clients.Get(provider);
        try
        {
            // A new message for every attempt: an HttpRequestMessage cannot be sent twice, and the retry strategy sends again.
            using var response = await client.Pipeline.ExecuteAsync(async token =>
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions") { Content = JsonContent.Create(BuildBody(request)) };
                if (!string.IsNullOrEmpty(provider.ApiKey))
                    message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
                return await client.Http.SendAsync(message, token);
            }, ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("LLM provider {Provider} answered {Status}", provider.Name, (int)response.StatusCode);
                return Failed(ProviderFailure.HttpError);
            }
            return await ParseAsync(response, provider.Name, request.Model, ct);
        }
        catch (Exception ex) when (IsTimeout(ex, ct))
        {
            logger.LogWarning("LLM provider {Provider} timed out", provider.Name);
            return Failed(ProviderFailure.Timeout);
        }
        catch (BrokenCircuitException)
        {
            logger.LogWarning("LLM provider {Provider} is shut off by its circuit breaker", provider.Name);
            return Failed(ProviderFailure.CircuitOpen);
        }
        catch (RateLimiterRejectedException)
        {
            logger.LogWarning("LLM provider {Provider} is at its concurrency limit", provider.Name);
            return Failed(ProviderFailure.Overloaded);
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("LLM provider {Provider} could not be reached", provider.Name);
            return Failed(ProviderFailure.Unreachable);
        }
    }

    private static ProviderCallResult Failed(ProviderFailure failure) => new(null, failure);

    // Our own cancellation (the caller went away) is not a timeout and must not be swallowed.
    private static bool IsTimeout(Exception ex, CancellationToken ct) =>
        !ct.IsCancellationRequested && (ex is TimeoutRejectedException or TaskCanceledException);

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

    private async Task<ProviderCallResult> ParseAsync(HttpResponseMessage response, string provider, string model, CancellationToken ct)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            var choice = root.GetProperty("choices")[0];
            var content = choice.GetProperty("message").GetProperty("content").GetString();
            if (content is null) return Failed(ProviderFailure.BadResponse);

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
            return new ProviderCallResult(new LlmResponse(id, answeredModel, content, finish, prompt, completion, provider), ProviderFailure.None);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            logger.LogWarning("LLM provider {Provider} sent a response we could not read", provider);
            return Failed(ProviderFailure.BadResponse);
        }
    }
}
