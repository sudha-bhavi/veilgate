using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using VeilGate.Application.Llm;

namespace VeilGate.Infrastructure.Llm;

public static class LlmServiceCollectionExtensions
{
    /// <summary>Registers one HTTP client per configured provider, each wrapped in the resilience pipeline of Backend doc 6.5.</summary>
    public static IServiceCollection AddLlmGateway(this IServiceCollection services, LlmOptions options)
    {
        services.AddSingleton<IOptions<LlmOptions>>(Options.Create(options));

        foreach (var provider in options.Providers)
        {
            var baseUri = ValidatedBaseUri(provider, options);
            services.AddHttpClient(LlmGateway.ClientName(provider.Name), client =>
                {
                    client.BaseAddress = baseUri;
                    client.Timeout = Timeout.InfiniteTimeSpan;                 // the resilience pipeline owns all timeouts
                    client.MaxResponseContentBufferSize = 4 * 1024 * 1024;     // a provider cannot make us read an endless body
                })
                .ConfigurePrimaryHttpMessageHandler(() => options.AllowPrivateNetworks
                    ? new SocketsHttpHandler()
                    : new SocketsHttpHandler { ConnectCallback = SsrfGuard.ConnectAsync })
                .AddResilienceHandler($"llm-{provider.Name}", builder => ConfigurePipeline(builder, provider, options));
        }

        services.AddSingleton<ILlmGateway, LlmGateway>();
        return services;
    }

    private static Uri ValidatedBaseUri(LlmProviderOptions provider, LlmOptions options)
    {
        if (string.IsNullOrWhiteSpace(provider.Name)) throw new InvalidOperationException("Every LLM provider needs a Name.");
        if (!Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var uri))
            throw new InvalidOperationException($"LLM provider '{provider.Name}' needs an absolute BaseUrl.");
        if (!options.AllowPrivateNetworks && uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException($"LLM provider '{provider.Name}' must use https.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");   // trailing slash so relative paths append instead of replacing
    }

    /// <summary>
    /// The order matters. A call goes in through the outer strategies first:
    /// 1. concurrency limit  2. total timeout  3. retry  4. circuit breaker  5. per-attempt timeout.
    /// </summary>
    private static void ConfigurePipeline(ResiliencePipelineBuilder<HttpResponseMessage> builder, LlmProviderOptions provider, LlmOptions options)
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

        builder.AddTimeout(TimeSpan.FromSeconds(provider.AttemptTimeoutSeconds));
    }
}
