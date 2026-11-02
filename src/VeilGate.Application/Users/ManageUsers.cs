using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;   // Paged
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Users;

/// <summary>What the manage actions have in common, so each rule lives in one place.</summary>
public static class UserRules
{
    public static readonly Error SelfChange = Error.Conflict("user.self_change", "You cannot do this to your own account. Ask another Tenant Admin.");
    public static readonly Error LastAdmin = Error.Conflict("user.last_admin", "The organization must keep at least one active Tenant Admin.");

    /// <summary>True when this person is an active Tenant Admin and nobody else is, so removing their admin powers would lock everyone out.</summary>
    public static async Task<bool> IsLastActiveAdminAsync(IUserRepository users, User target, CancellationToken ct) =>
        target is { Role: UserRole.TenantAdmin, Status: UserStatus.Active } && await users.CountActiveAdminsExceptAsync(target.Id, ct) == 0;

    public static async Task<UserView> ViewAsync(IMfaFactorRepository factors, User user, CancellationToken ct) =>
        UserView.From(user, await factors.GetTotpForUserAsync(user.Id, ct) is { IsConfirmed: true });

    /// <summary>Signs the person out of every device. The refresh cookie of a revoked session no longer works.</summary>
    public static async Task EndSessionsAsync(ISessionRepository sessions, Guid userId, string reason, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var session in await sessions.ListActiveForUserAsync(userId, now, ct)) session.Revoke(reason, now);
    }
}

/// <summary>GET /users (Backend doc 5.1): search, filter by role and status, page.</summary>
public sealed class ListUsersHandler(IUserRepository users)
{
    public async Task<Result<Paged<UserView>>> HandleAsync(
        string? role, string? status, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        UserRole? parsedRole = null;
        if (!string.IsNullOrEmpty(role))
        {
            if (!Enum.GetNames<UserRole>().Contains(role)) return Error.Validation("validation.failed", "Unknown role.");
            parsedRole = Enum.Parse<UserRole>(role);
        }

        UserStatus? parsedStatus = null;
        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.GetNames<UserStatus>().Contains(status)) return Error.Validation("validation.failed", "Unknown status.");
            parsedStatus = Enum.Parse<UserStatus>(status);
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (rows, total) = await users.ListAsync(parsedRole, parsedStatus, search, page, pageSize, ct);
        return new Paged<UserView>(rows.Select(r => UserView.From(r.User, r.HasMfa)).ToList(), page, pageSize, total);
    }
}

public sealed record ChangeUserRoleCommand(Guid UserId, string Role, UserActor Actor);

/// <summary>PATCH /users/{id}. The controller checks the recent-MFA proof. Another tenant's user is simply "not found".</summary>
public sealed class ChangeUserRoleHandler(IUserRepository users, IMfaFactorRepository factors, IClock clock, UserAuditor auditor)
{
    public async Task<Result<UserView>> HandleAsync(ChangeUserRoleCommand cmd, CancellationToken ct = default)
    {
        if (!UserRoles.TryParseAssignable(cmd.Role, out var newRole))
            return Error.Validation("validation.failed", "Choose a role from the list.");
        if (cmd.UserId == cmd.Actor.UserId) return UserRules.SelfChange;

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null) return Error.NotFound();
        if (newRole != UserRole.TenantAdmin && await UserRules.IsLastActiveAdminAsync(users, user, ct)) return UserRules.LastAdmin;

        var changed = user.ChangeRole(newRole, clock.UtcNow);
        if (changed.IsFailure) return changed.Error;

        await users.SaveChangesAsync(ct);
        // Audit details may only use the fixed list of keys in AuditEvent.AllowedDetailKeys, so this records the new role.
        await auditor.WriteAsync(cmd.Actor, "user.role_change", user.Id, new Dictionary<string, string> { ["role"] = newRole.ToString() }, ct);
        return await UserRules.ViewAsync(factors, user, ct);
    }
}

/// <summary>One person, one action: disable, enable or reset two-step sign-in.</summary>
public sealed record UserActionCommand(Guid UserId, UserActor Actor);

public sealed class DisableUserHandler(
    IUserRepository users, IMfaFactorRepository factors, ISessionRepository sessions, IClock clock, UserAuditor auditor)
{
    public async Task<Result<UserView>> HandleAsync(UserActionCommand cmd, CancellationToken ct = default)
    {
        if (cmd.UserId == cmd.Actor.UserId) return UserRules.SelfChange;

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null) return Error.NotFound();
        if (await UserRules.IsLastActiveAdminAsync(users, user, ct)) return UserRules.LastAdmin;

        var now = clock.UtcNow;
        var disabled = user.Disable(now);   // also raises the token version, so their access tokens stop working at once
        if (disabled.IsFailure) return disabled.Error;

        await UserRules.EndSessionsAsync(sessions, user.Id, "user_disabled", now, ct);
        await users.SaveChangesAsync(ct);   // the user and their sessions, together
        await auditor.WriteAsync(cmd.Actor, "user.disable", user.Id, null, ct);
        return await UserRules.ViewAsync(factors, user, ct);
    }
}

public sealed class EnableUserHandler(IUserRepository users, IMfaFactorRepository factors, IClock clock, UserAuditor auditor)
{
    public async Task<Result<UserView>> HandleAsync(UserActionCommand cmd, CancellationToken ct = default)
    {
        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null) return Error.NotFound();

        var enabled = user.Enable(clock.UtcNow);
        if (enabled.IsFailure) return enabled.Error;

        await users.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "user.enable", user.Id, null, ct);
        return await UserRules.ViewAsync(factors, user, ct);
    }
}

/// <summary>
/// POST /users/{id}/mfa/reset. Removes the person's authenticator and recovery codes, ends every session and kills their tokens.
/// At the next sign-in they enter their password and are taken through two-step setup again. The controller checks the recent-MFA proof.
/// </summary>
public sealed class ResetMfaHandler(
    IUserRepository users, IMfaFactorRepository factors, IRecoveryCodeRepository recoveryCodes, ISessionRepository sessions,
    ISecurityEventWriter securityEvents, IClock clock, UserAuditor auditor, ILogger<ResetMfaHandler> logger)
{
    public async Task<Result> HandleAsync(UserActionCommand cmd, CancellationToken ct = default)
    {
        if (cmd.UserId == cmd.Actor.UserId) return Result.Failure(UserRules.SelfChange);

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null) return Result.Failure(Error.NotFound());
        if (user.Status != UserStatus.Active)
            return Result.Failure(Error.Conflict("user.not_active", "Only active people have two-step sign-in to reset."));

        var now = clock.UtcNow;
        var factor = await factors.GetTotpForUserAsync(user.Id, ct);
        if (factor is not null) factors.Remove(factor);
        recoveryCodes.RemoveRange(await recoveryCodes.ListForUserAsync(user.Id, ct));

        user.InvalidateTokens(now);
        await UserRules.EndSessionsAsync(sessions, user.Id, "mfa_reset", now, ct);
        await users.SaveChangesAsync(ct);   // factor, codes, user and sessions: all or nothing
        await auditor.WriteAsync(cmd.Actor, "user.mfa_reset", user.Id, null, ct);

        var raised = SecurityEvent.Create(user.TenantId, now, Severity.Medium, "mfa.reset", userId: user.Id, ip: cmd.Actor.Ip,
            details: new Dictionary<string, string> { ["reason"] = "admin_reset" });   // who did it is in the audit event above
        if (raised.IsSuccess) await securityEvents.WriteAsync(raised.Value, ct);
        else logger.LogError("Could not build security event mfa.reset: {Code}", raised.Error.Code);

        return Result.Success();
    }
}
