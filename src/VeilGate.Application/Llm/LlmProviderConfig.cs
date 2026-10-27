using System.Text;

namespace VeilGate.Application.Llm;

/// <summary>
/// A provider ready to be called: the stored settings plus the decrypted API key. It exists only in memory for the length of a request.
/// Its text form leaves the key out, so logging one by accident does not leak it.
/// </summary>
public sealed record LlmProviderConfig(
    Guid Id, Guid TenantId, string Name, string BaseUrl, string ApiKey, int TimeoutSeconds, int Priority,
    IReadOnlyList<string> Models, int? AttemptTimeoutSeconds = null)
{
    /// <summary>Time allowed for one attempt (Backend doc 6.5: 30 s), never more than the whole call may take.</summary>
    public int AttemptTimeout => AttemptTimeoutSeconds ?? Math.Min(30, TimeoutSeconds);

    public bool Serves(string model) => Models.Contains(model, StringComparer.Ordinal);

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Name = {Name}, Id = {Id}");
        return true;
    }
}

/// <summary>
/// The providers a tenant has connected, as the gateway needs them. Always for the current tenant: row-level security decides,
/// not a parameter, so there is no way to ask for another tenant's providers.
/// </summary>
public interface ILlmProviderCatalog
{
    /// <summary>Enabled providers that serve the model, in failover order (lowest priority number first), with their keys decrypted.</summary>
    Task<IReadOnlyList<LlmProviderConfig>> GetForModelAsync(string model, CancellationToken ct = default);

    /// <summary>Whether any enabled provider serves the model. Does not decrypt anything.</summary>
    Task<bool> ServesModelAsync(string model, CancellationToken ct = default);

    /// <summary>One provider by id, enabled or not, with its key decrypted. Null if it does not exist in this tenant or its key cannot be read.</summary>
    Task<LlmProviderConfig?> FindAsync(Guid providerId, CancellationToken ct = default);
}

/// <param name="ErrorCategory">A short fixed word (timeout, unreachable, circuit_open, overloaded, http_error, invalid_response). Never the provider's own error text.</param>
public sealed record ProviderTestResult(bool Ok, long LatencyMs, string? ErrorCategory);

/// <summary>Sends a tiny harmless request through the same resilient path real calls use, to show whether a provider works.</summary>
public interface ILlmProviderTester
{
    Task<ProviderTestResult> TestAsync(LlmProviderConfig provider, CancellationToken ct = default);
}
