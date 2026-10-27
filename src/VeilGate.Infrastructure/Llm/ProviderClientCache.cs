using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using VeilGate.Application.Llm;

namespace VeilGate.Infrastructure.Llm;

/// <summary>An HTTP client for one provider and the resilience pipeline around it. Kept together because the pipeline's circuit breaker has state.</summary>
public sealed record ProviderClient(HttpClient Http, ResiliencePipeline<HttpResponseMessage> Pipeline);

/// <summary>
/// One <see cref="ProviderClient"/> per provider, built when first needed and kept, so a provider's circuit breaker and concurrency
/// limit mean something across requests (Backend doc 6.5). Providers are no longer known at startup, so this replaces the named
/// clients the configuration list used to create. If a provider's address or timeouts are edited, it gets a fresh client; changing
/// only its API key does not reset anything, because the key is added per request.
/// </summary>
public sealed class ProviderClientCache(IOptions<LlmOptions> options, Func<LlmProviderConfig, HttpMessageHandler>? handlerFactory = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, (string Fingerprint, ProviderClient Client)> _clients = new();

    public ProviderClient Get(LlmProviderConfig provider)
    {
        var fingerprint = $"{provider.BaseUrl}|{provider.TimeoutSeconds}|{provider.AttemptTimeout}";
        lock (_gate)
        {
            if (_clients.TryGetValue(provider.Id, out var existing) && existing.Fingerprint == fingerprint) return existing.Client;
            var created = Create(provider);
            _clients[provider.Id] = (fingerprint, created);
            return created;
        }
    }

    private ProviderClient Create(LlmProviderConfig provider)
    {
        var o = options.Value;
        var handler = handlerFactory?.Invoke(provider) ?? DefaultHandler(o);
        var http = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(provider.BaseUrl.TrimEnd('/') + "/"),   // trailing slash so relative paths append instead of replacing
            Timeout = Timeout.InfiniteTimeSpan,                           // the resilience pipeline owns all timeouts
            MaxResponseContentBufferSize = 4 * 1024 * 1024,               // a provider cannot make us read an endless body
        };

        var builder = new ResiliencePipelineBuilder<HttpResponseMessage>();
        ConfigurePipeline(builder, provider, o);
        return new ProviderClient(http, builder.Build());
    }

    private static HttpMessageHandler DefaultHandler(LlmOptions options)
    {
        // Connections are recycled so a changed DNS answer is noticed, and the destination address is checked on every new
        // connection, so a name that was public when the provider was saved and private later ("DNS rebinding") is still stopped.
        var handler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) };
        if (!options.AllowPrivateNetworks) handler.ConnectCallback = SsrfGuard.ConnectAsync;
        return handler;
    }

    /// <summary>
    /// The order matters. A call goes in through the outer strategies first:
    /// 1. concurrency limit  2. total timeout  3. retry  4. circuit breaker  5. per-attempt timeout.
    /// </summary>
    private static void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder, LlmProviderConfig provider, LlmOptions options)
    {
        builder.AddConcurrencyLimiter(options.MaxConcurrentCalls, queueLimit: 0);
        builder.AddTimeout(TimeSpan.FromSeconds(provider.TimeoutSeconds));

        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            Delay = TimeSpan.FromMilliseconds(options.RetryBaseDelayMs),
            // Retry only what can succeed on a second try: timeouts, network errors, 408, 429 and 5xx. Never a 400 or 401.
            ShouldHandle = args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
        });

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = options.BreakerMinimumThroughput,
            SamplingDuration = TimeSpan.FromSeconds(30),
            BreakDuration = TimeSpan.FromSeconds(30),
            ShouldHandle = args => ValueTask.FromResult(HttpClientResiliencePredicates.IsTransient(args.Outcome)),
        });

        builder.AddTimeout(TimeSpan.FromSeconds(provider.AttemptTimeout));
    }
}
