namespace VeilGate.Infrastructure.Llm;

/// <summary>
/// Settings that apply to every provider call, in the "Llm" configuration section. The providers themselves are not here:
/// each tenant connects its own (the llm_providers table, managed through /api/v1/llm-providers).
/// </summary>
public sealed class LlmOptions
{
    public const string Section = "Llm";

    /// <summary>First retry waits about this long, then doubles (with random jitter). Tests set it to 1.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    /// <summary>The circuit breaker only judges a provider after this many calls in 30 seconds (Backend doc 6.5: 20).</summary>
    public int BreakerMinimumThroughput { get; set; } = 20;

    /// <summary>Most calls in flight at once per provider (Backend doc 6.5: 50).</summary>
    public int MaxConcurrentCalls { get; set; } = 50;

    /// <summary>
    /// Development only. When false (the default) a provider must use https and may not resolve to a private,
    /// loopback or cloud-metadata address (SSRF defense, Backend doc 5.1). It is checked when a provider is saved
    /// and again on every connection.
    /// </summary>
    public bool AllowPrivateNetworks { get; set; }
}
