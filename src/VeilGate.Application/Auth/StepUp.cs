using FluentValidation;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

/// <param name="SessionId">The "sid" of the access token being stepped up. The new token keeps it, so it stays the same session.</param>
/// <param name="TokenVersion">The "ver" claim of that token. Must still match the user's current version.</param>
/// <param name="Code">The 6-digit code from the authenticator app, or a one-time recovery code.</param>
/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record StepUpCommand(Guid UserId, Guid TenantId, Guid SessionId, int TokenVersion, string Code, string? Ip = null);

public sealed record StepUpResult(string AccessToken);

public sealed class StepUpValidator : AbstractValidator<StepUpCommand>
{
    public StepUpValidator() => RuleFor(x => x.Code).NotEmpty().MaximumLength(12);   // "XXXXX-XXXXX" is 11 characters
}

/// <summary>
/// A signed-in person proves a second factor again, so the most sensitive actions (publish, rollback) accept them for the
/// next few minutes. Returns a fresh access token for the same session with auth_time = now.
/// </summary>
public sealed class StepUpHandler(
    IUserRepository users, IMfaFactorRepository factors, IRecoveryCodeRepository recoveryCodeStore,
    ITenantContext tenant, ISecretProtector protector, ITokenService tokens, IClock clock, AuthAuditor auditor,
    IValidator<StepUpCommand> validator)
{
    private const int RecoveryCodeLength = 10;   // letters and digits, not counting the dash
    private static readonly Error InvalidToken = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    public async Task<Result<StepUpResult>> HandleAsync(StepUpCommand cmd, CancellationToken ct = default)
    {
        var check = await validator.ValidateAsync(cmd, ct);
        if (!check.IsValid) return Error.Validation("validation.failed", check.Errors[0].ErrorMessage);

        if (!tenant.IsSet) tenant.Set(cmd.TenantId);

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || user.TokenVersion != cmd.TokenVersion
            || !await users.IsTenantActiveAsync(ct))
            return InvalidToken;

        var now = clock.UtcNow;
        if (user.IsLocked(now))
        {
            await auditor.DeniedAsync(user, AuthAuditor.StepUp, cmd.Ip, ct);
            return LoginHandler.AccountLocked;
        }

        var factor = await factors.GetTotpForUserAsync(user.Id, ct);
        if (factor is not { IsConfirmed: true })
            return Error.Forbidden("mfa.not_enrolled", "Set up MFA first.");

        string method;
        if (RecoveryCodes.Normalize(cmd.Code).Length == RecoveryCodeLength)
        {
            var stored = await recoveryCodeStore.FindByHashAsync(user.Id, RecoveryCodes.Hash(cmd.Code), ct);
            if (stored is null || stored.MarkUsed(now).IsFailure)
                return await RejectAsync(user, now, "recovery_code", cmd.Ip, ct);

            method = "recovery";
        }
        else
        {
            var secret = protector.Unprotect(factor.ProtectedSecret, StartTotpEnrollmentHandler.ProtectionContext(user.Id));
            var window = Totp.Verify(secret, cmd.Code, now.ToUnixTimeSeconds(), factor.LastUsedCounter);
            if (window is null) return await RejectAsync(user, now, "code", cmd.Ip, ct);

            var used = factor.RecordUse(window.Value, now);   // remember the window so this code can't be replayed
            if (used.IsFailure) return used.Error;

            method = "otp";
        }

        await factors.SaveChangesAsync(ct);   // one DbContext per request: this also saves a used recovery code
        await auditor.SucceededAsync(user.TenantId, user.Id, AuthAuditor.StepUp, cmd.Ip, method, ct);

        var accessToken = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(),
            SessionId: cmd.SessionId,
            Amr: ["pwd", method],
            AuthTime: now,
            TokenVersion: user.TokenVersion), now);

        return new StepUpResult(accessToken);
    }

    // A wrong code counts like a wrong password: 5 in a row lock the account for 15 minutes.
    private async Task<Result<StepUpResult>> RejectAsync(User user, DateTimeOffset now, string reason, string? ip, CancellationToken ct)
    {
        user.RegisterFailedLogin(now);
        await users.SaveChangesAsync(ct);
        await auditor.FailedAsync(user, AuthAuditor.StepUp, reason, ip, ct);
        return ConfirmTotpHandler.InvalidCode;
    }
}