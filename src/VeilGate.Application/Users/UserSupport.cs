using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;

namespace VeilGate.Application.Users;

/// <summary>Who is acting and from where. Both come from the verified token and the connection, never from the body.</summary>
public sealed record UserActor(Guid UserId, string? Ip);

/// <summary>What the API shows of a user. Never a password hash or a token.</summary>
public sealed record UserView(Guid Id, string Name, string Email, string Role, string Status, bool HasMfa, DateTimeOffset? LastLoginAt)
{
    public static UserView From(User u, bool hasMfa) =>
        new(u.Id, u.DisplayName, u.Email, u.Role.ToString(), u.Status.ToString(), hasMfa, u.LastLoginAt);
}

public static class UserRoles
{
    /// <summary>The roles a Tenant Admin may hand out: every role except PlatformAdmin, which belongs to the VeilGate operator.</summary>
    public static bool TryParseAssignable(string? role, out UserRole parsed)
    {
        parsed = default;
        // Enum.TryParse would also accept "3" or "1,2". Only the exact role names count.
        if (role is null || !Enum.GetNames<UserRole>().Contains(role)) return false;
        parsed = Enum.Parse<UserRole>(role);
        return parsed != UserRole.PlatformAdmin;
    }
}

/// <summary>Writes user-management events to the audit log: who did what to which user. Never an email address, a password or a token.</summary>
public sealed class UserAuditor(IAuditWriter audit, ITenantContext tenant, IClock clock, ILogger<UserAuditor> logger)
{
    public async Task WriteAsync(UserActor actor, string action, Guid userId, IReadOnlyDictionary<string, string>? details, CancellationToken ct)
    {
        var created = AuditEvent.Create(tenant.TenantId, clock.UtcNow, "user", actor.UserId, action, "user", userId, "success", actor.Ip, details);
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }
}