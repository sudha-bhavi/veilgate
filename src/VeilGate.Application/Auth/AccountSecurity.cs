using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

/// <param name="CurrentSessionId">The "sid" of the request. This session stays signed in; every other one is ended.</param>
/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record ChangePasswordCommand(
    Guid UserId, Guid CurrentSessionId, string? CurrentPassword, string? NewPassword, string? Ip = null);

/// <summary>
/// POST /auth/password/change. The current password is the proof of identity, so a wrong one counts toward the lockout like a wrong
/// sign-in. On success every other device is signed out: their sessions end and the token version goes up.
/// </summary>
public sealed class ChangePasswordHandler(
    IUserRepository users, ISessionRepository sessions, IPasswordHasher hasher, ISecurityEventWriter securityEvents,
    IClock clock, AuthAuditor auditor, ILogger<ChangePasswordHandler> logger)
{
    public const int MaxPasswordInput = 256;   // an upper bound, so a giant input cannot burn hashing time

    public static readonly Error CurrentIncorrect = Error.Validation("password.current_incorrect", "Your current password is incorrect.");
    public static readonly Error SameAsCurrent = Error.Validation("password.same_as_current", "Choose a password you are not using now.");

    public async Task<Result> HandleAsync(ChangePasswordCommand cmd, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(cmd.CurrentPassword) || cmd.CurrentPassword.Length > MaxPasswordInput)
            return Result.Failure(CurrentIncorrect);
        if (string.IsNullOrEmpty(cmd.NewPassword) || cmd.NewPassword.Length > MaxPasswordInput)
            return Result.Failure(Error.Validation("password.weak", "Choose a new password."));

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
            return Result.Failure(Error.Unauthorized("auth.token_invalid", "Sign in again."));

        var now = clock.UtcNow;
        if (user.IsLocked(now))
        {
            await auditor.DeniedAsync(user, AuthAuditor.PasswordChange, cmd.Ip, ct);
            return Result.Failure(LoginHandler.AccountLocked);
        }

        // Count this attempt first, atomically; only then check the password (see TryReserveLoginAttemptAsync).
        if (!await users.TryReserveLoginAttemptAsync(user, now, ct))
        {
            await auditor.DeniedAsync(user, AuthAuditor.PasswordChange, cmd.Ip, ct);
            return Result.Failure(LoginHandler.AccountLocked);
        }

        if (user.PasswordHash is null || !hasher.Verify(cmd.CurrentPassword, user.PasswordHash))
        {
            await auditor.FailedAsync(user, AuthAuditor.PasswordChange, "password", cmd.Ip, ct);
            return Result.Failure(CurrentIncorrect);
        }

        var weak = PasswordPolicy.Validate(cmd.NewPassword, user.Email);
        if (weak.IsFailure) return weak;
        if (hasher.Verify(cmd.NewPassword, user.PasswordHash)) return Result.Failure(SameAsCurrent);

        var changed = user.ChangePassword(hasher.Hash(cmd.NewPassword), now);
        if (changed.IsFailure) return changed;

        var ended = 0;
        foreach (var session in await sessions.ListActiveForUserAsync(user.Id, now, ct))
        {
            if (session.Id == cmd.CurrentSessionId) continue;
            session.Revoke("password_change", now);
            ended++;
        }

        await users.SaveChangesAsync(ct);   // the user and the ended sessions, together
        await auditor.SessionsEndedAsync(user.TenantId, user.Id, AuthAuditor.PasswordChange, cmd.Ip, ended, ct);

        var raised = SecurityEvent.Create(user.TenantId, now, Severity.Info, "auth.password_changed", userId: user.Id, ip: cmd.Ip);
        if (raised.IsSuccess) await securityEvents.WriteAsync(raised.Value, ct);
        else logger.LogError("Could not build security event auth.password_changed: {Code}", raised.Error.Code);

        return Result.Success();
    }
}

/// <param name="AddedAt">When the authenticator app was confirmed, or null when none is set up.</param>
public sealed record MfaStatus(DateTimeOffset? AddedAt, int RecoveryCodesLeft, int RecoveryCodesTotal);

/// <summary>GET /auth/mfa/factors: what the signed-in person has set up, for the MFA tab.</summary>
public sealed class GetMfaStatusHandler(IMfaFactorRepository factors, IRecoveryCodeRepository codes)
{
    public async Task<MfaStatus> HandleAsync(Guid userId, CancellationToken ct = default)
    {
        var factor = await factors.GetTotpForUserAsync(userId, ct);
        var confirmed = factor is { IsConfirmed: true };
        if (!confirmed) return new MfaStatus(null, 0, 0);

        var all = await codes.ListForUserAsync(userId, ct);
        return new MfaStatus(factor!.ConfirmedAt, all.Count(c => !c.IsUsed), all.Count);
    }
}

/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record RegenerateRecoveryCodesCommand(Guid UserId, string? Ip = null);

/// <summary>
/// POST /auth/mfa/recovery-codes/regenerate. Replaces every recovery code, used or not: the old ones stop working at once.
/// The readable codes are returned once and never stored. The controller checks the recent-MFA proof.
/// </summary>
public sealed class RegenerateRecoveryCodesHandler(
    IUserRepository users, IMfaFactorRepository factors, IRecoveryCodeRepository codes, ISecurityEventWriter securityEvents,
    IClock clock, AuthAuditor auditor, ILogger<RegenerateRecoveryCodesHandler> logger)
{
    public async Task<Result<IReadOnlyList<string>>> HandleAsync(RegenerateRecoveryCodesCommand cmd, CancellationToken ct = default)
    {
        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
            return Error.Unauthorized("auth.token_invalid", "Sign in again.");

        if (await factors.GetTotpForUserAsync(user.Id, ct) is not { IsConfirmed: true })
            return Error.Conflict("mfa.not_enrolled", "Set up two-step sign-in first.");

        var now = clock.UtcNow;
        var readable = RecoveryCodes.Generate();
        var fresh = new List<RecoveryCode>(readable.Count);
        foreach (var code in readable)
        {
            var made = RecoveryCode.Create(user.TenantId, user.Id, RecoveryCodes.Hash(code), now);
            if (made.IsFailure) return made.Error;
            fresh.Add(made.Value);
        }

        codes.RemoveRange(await codes.ListForUserAsync(user.Id, ct));
        codes.AddRange(fresh);
        await codes.SaveChangesAsync(ct);   // the old codes go and the new ones arrive in one save, or not at all
        await auditor.SucceededAsync(user.TenantId, user.Id, AuthAuditor.RecoveryCodesRegenerate, cmd.Ip, null, ct);

        var raised = SecurityEvent.Create(user.TenantId, now, Severity.Info, "mfa.recovery_codes_regenerated", userId: user.Id, ip: cmd.Ip);
        if (raised.IsSuccess) await securityEvents.WriteAsync(raised.Value, ct);
        else logger.LogError("Could not build security event mfa.recovery_codes_regenerated: {Code}", raised.Error.Code);

        return Result.Success(readable);
    }
}
