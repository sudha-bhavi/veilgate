namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Language model providers, in the "Llm" configuration section. In M4 they move into the llm_providers table
/// (editable per tenant in the console); until then this one list serves every tenant.
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    public List<LlmProviderOptions> Providers { get; set; } = [];

    /// <summary>First retry waits about this long, then doubles (with random jitter). Tests set it to 1.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>The circuit breaker only judges a provider after this many calls in 30 seconds (Backend doc 6.5: 20).</summary>
    public int BreakerMinimumThroughput { get; set; } = 20;

    /// <summary>Most calls in flight at once per provider (Backend doc 6.5: 50).</summary>
    public int MaxConcurrentCalls { get; set; } = 50;

    /// <summary>
    /// Development only. When false (the default) a provider must use https and may not resolve to a private,
    /// loopback or cloud-metadata address (SSRF defense, Backend doc 5.1).
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}

public sealed class LlmProviderOptions
{
    public string Name { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Time allowed for the whole call including retries.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>Time allowed for one attempt.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 30;

    /// <summary>Lower number = tried first. Failover goes to the next.</summary>
    public int Priority { get; set; } = 100;

    /// <summary>Models this provider serves. Empty means any model.</summary>
    public List<string> Models { get; set; } = [];
}
