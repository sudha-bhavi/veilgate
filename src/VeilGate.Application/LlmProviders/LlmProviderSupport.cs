using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;
using VeilGate.Domain.LlmProviders;

namespace VeilGate.Application.LlmProviders;

/// <summary>Who is acting and from where. Both come from the verified token and the connection, never from the body.</summary>
public sealed record LlmProviderActor(Guid UserId, string? Ip);

/// <summary>What the API shows of a provider. Never the key, only its last four characters.</summary>
public sealed record LlmProviderView(
    Guid Id, string Name, string BaseUrl, string KeyLast4, string[] Models, int TimeoutSeconds, int Priority, bool Enabled, DateTimeOffset CreatedAt)
{
    public static LlmProviderView From(LlmProvider p) =>
        new(p.Id, p.Name, p.BaseUrl, p.KeyLast4, p.Models, p.TimeoutSeconds, p.Priority, p.Enabled, p.CreatedAt);
}

public static class LlmProviderKeys
{
    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 512;

    /// <summary>
    /// Bound into the encryption of a provider's API key, so the ciphertext only opens for this provider of this tenant.
    /// An attacker who can edit the table cannot move one provider's key onto another.
    /// </summary>
    public static string Context(Guid tenantId, Guid providerId) => $"llm-provider-key|{tenantId}|{providerId}";

    public static bool IsAcceptable(string? apiKey) => apiKey is { Length: >= MinKeyLength and <= MaxKeyLength };
}

/// <summary>Writes provider events to the audit log: who did what to which provider, never a key.</summary>
public sealed class LlmProviderAuditor(IAuditWriter audit, ITenantContext tenant, IClock clock, ILogger<LlmProviderAuditor> logger)
{
    public async Task WriteAsync(LlmProviderActor actor, string action, Guid? providerId, string? providerName, CancellationToken ct,
        string result = "success", string? reason = null)
    {
        var details = new Dictionary<string, string>();
        if (providerName is not null) details["provider"] = providerName.Length > 200 ? providerName[..200] : providerName;
        if (reason is not null) details["reason"] = reason;

        var created = AuditEvent.Create(tenant.TenantId, clock.UtcNow, "user", actor.UserId, action,
            "llm_provider", providerId, result, actor.Ip, details);
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }
}
