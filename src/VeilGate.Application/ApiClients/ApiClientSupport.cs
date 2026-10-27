using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.ApiClients;
using VeilGate.Domain.Audit;

namespace VeilGate.Application.ApiClients;

/// <summary>Who is acting, in what role, and from where. All three come from the verified token and the connection, never from the body.</summary>
public sealed record ApiClientActor(Guid UserId, string Role, string? Ip)
{
    public const string DeveloperRole = "Developer";

    /// <summary>A Developer sees and manages only the clients they created; a Tenant Admin sees all of the tenant's.</summary>
    public bool OwnsOrManages(ApiClient client) => Role != DeveloperRole || client.OwnerUserId == UserId;
}

/// <summary>What the API shows of a client. Never a secret and never a hash.</summary>
public sealed record ApiClientView(
    Guid Id, string ClientId, string Name, string[] Scopes, string Status, Guid? DefaultPolicyId,
    string[] IpAllowlist, int RateLimitPerMinute, Guid OwnerUserId, DateTimeOffset CreatedAt)
{
    public static ApiClientView From(ApiClient c) => new(
        c.Id, c.ClientId, c.Name, c.Scopes, c.Status.ToString(), c.DefaultPolicyId,
        c.IpAllowlist, c.RateLimitPerMinute, c.OwnerUserId, c.CreatedAt);
}

/// <summary>Writes API client events to the audit log: who did what to which client, never a secret.</summary>
public sealed class ApiClientAuditor(IAuditWriter audit, ITenantContext tenant, IClock clock, ILogger<ApiClientAuditor> logger)
{
    public async Task WriteAsync(ApiClientActor actor, string action, ApiClient client, CancellationToken ct)
    {
        var created = AuditEvent.Create(tenant.TenantId, clock.UtcNow, "user", actor.UserId, action,
            "api_client", client.Id, "success", actor.Ip,
            new Dictionary<string, string> { ["clientId"] = client.ClientId });
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }
}
