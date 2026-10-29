using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;

namespace VeilGate.Application.Auth;

/// <summary>
/// Writes sign-in events to the tenant's audit log: sign-in, sign-out, MFA setup and step-up, and the failures of each.
/// Only the person, the result, a short reason and the address are recorded, never a password or a code.
/// Attempts with an email nobody uses are not recorded: no tenant owns them, and recording them would tell a guesser nothing new.
/// </summary>
public sealed class AuthAuditor(IAuditWriter audit, ISecurityEventWriter securityEvents, IClock clock, ILogger<AuthAuditor> logger)
{
    public const string SignIn = "auth.login";
    public const string SignOut = "auth.logout";
    public const string MfaEnroll = "auth.mfa_enroll";
    public const string StepUp = "auth.step_up";

    /// <param name="method">How the second factor was proved ("otp" or "recovery"), when there was one.</param>
    public Task SucceededAsync(Guid tenantId, Guid userId, string action, string? ip, string? method, CancellationToken ct) =>
        WriteAsync(tenantId, userId, action, "success", ip,
            method is null ? null : new Dictionary<string, string> { ["method"] = method }, ct);

    /// <summary>The account was locked before this attempt, so it was not even checked.</summary>
    public Task DeniedAsync(User user, string action, string? ip, CancellationToken ct) =>
        WriteAsync(user.TenantId, user.Id, action, "denied", ip, new Dictionary<string, string> { ["reason"] = "locked" }, ct);

    /// <summary>
    /// A failed attempt. When it counted toward the lock (a wrong password or code, after RegisterFailedLogin) and it is the
    /// one that locked the account, a security event is raised too, so repeated guessing shows up for the Security Officer.
    /// </summary>
    public async Task FailedAsync(User user, string action, string reason, string? ip, CancellationToken ct, bool counted = true)
    {
        await WriteAsync(user.TenantId, user.Id, action, "failure", ip, new Dictionary<string, string> { ["reason"] = reason }, ct);

        var now = clock.UtcNow;
        // The handlers stop earlier when the account was already locked, so a counted failure that leaves it locked just locked it.
        if (!counted || !user.IsLocked(now)) return;
        var locked = SecurityEvent.Create(user.TenantId, now, Severity.Medium, "auth.account_locked", userId: user.Id, ip: ip,
            details: new Dictionary<string, string> { ["reason"] = reason, ["count"] = User.MaxFailedAttempts.ToString() });
        if (locked.IsSuccess) await securityEvents.WriteAsync(locked.Value, ct);
        else logger.LogError("Could not build security event auth.account_locked: {Code}", locked.Error.Code);
    }

    private async Task WriteAsync(
        Guid tenantId, Guid userId, string action, string result, string? ip, IReadOnlyDictionary<string, string>? details, CancellationToken ct)
    {
        var created = AuditEvent.Create(tenantId, clock.UtcNow, "user", userId, action, "user", userId, result, ip, details);
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }
}
