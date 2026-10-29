using FluentValidation;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

/// <param name="TokenVersion">The "ver" claim from the temporary MFA token. Must still match the user's current version.</param>
/// <param name="Code">Either the 6-digit code from the authenticator app, or a one-time recovery code.</param>
/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record VerifyMfaCommand(Guid UserId, Guid TenantId, int TokenVersion, string Code, string? Ip = null);

public sealed record VerifyMfaResult(string AccessToken, string RefreshToken, DateTimeOffset RefreshExpiresAt);

public sealed class VerifyMfaValidator : AbstractValidator<VerifyMfaCommand>
{
    public VerifyMfaValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(12);   // "XXXXX-XXXXX" is 11 characters
    }
}

/// <summary>Step 2 of sign-in: an MFA code (or a recovery code) turns the temporary token into a real access token.</summary>
public sealed class VerifyMfaHandler(
    IUserRepository users, IMfaFactorRepository factors, IRecoveryCodeRepository recoveryCodeStore,ISessionRepository sessions,
    ITenantContext tenant, ISecretProtector protector, ITokenService tokens, IClock clock, AuthAuditor auditor,
    IValidator<VerifyMfaCommand> validator)
{
    private const int RecoveryCodeLength = 10;   // letters and digits, not counting the dash
    private static readonly Error InvalidToken = Error.Unauthorized("auth.token_invalid", "Sign in again.");

    public async Task<Result<VerifyMfaResult>> HandleAsync(VerifyMfaCommand cmd, CancellationToken ct = default)
    {
        var check = await validator.ValidateAsync(cmd, ct);
        if (!check.IsValid) return Error.Validation("validation.failed", check.Errors[0].ErrorMessage);

        if (!tenant.IsSet) tenant.Set(cmd.TenantId);

        // Is the temporary token still good? The user must exist, be active, and still be on the same token version.
        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || user.TokenVersion != cmd.TokenVersion
            || !await users.IsTenantActiveAsync(ct))
            return InvalidToken;

        var now = clock.UtcNow;
        if (user.IsLocked(now))
        {
            await auditor.DeniedAsync(user, AuthAuditor.SignIn, cmd.Ip, ct);
            return LoginHandler.AccountLocked;
        }

        var factor = await factors.GetTotpForUserAsync(user.Id, ct);
        if (factor is not { IsConfirmed: true })
            return Error.Forbidden("mfa.not_enrolled", "Set up MFA first.");

        string[] methods;
        if (RecoveryCodes.Normalize(cmd.Code).Length == RecoveryCodeLength)
        {
            // A backup code. Look it up by its hash; it must exist and must not have been used before.
            var stored = await recoveryCodeStore.FindByHashAsync(user.Id, RecoveryCodes.Hash(cmd.Code), ct);
            if (stored is null || stored.MarkUsed(now).IsFailure)
                return await RejectAsync(user, now, "recovery_code", cmd.Ip, ct);

            methods = ["pwd", "recovery"];
        }
        else
        {
            // A 6-digit code from the authenticator app.
            var secret = protector.Unprotect(factor.ProtectedSecret, StartTotpEnrollmentHandler.ProtectionContext(user.Id));
            var window = Totp.Verify(secret, cmd.Code, now.ToUnixTimeSeconds(), factor.LastUsedCounter);
            if (window is null) return await RejectAsync(user, now, "code", cmd.Ip, ct);

            var used = factor.RecordUse(window.Value, now);   // remember the window so this code can't be replayed
            if (used.IsFailure) return used.Error;

            methods = ["pwd", "otp"];
        }

               user.RegisterSuccessfulLogin(now);

        // Sign-in is complete, so this device gets a session. The browser keeps the raw token; we store only its hash.
        var refreshToken = RefreshTokens.Generate();
        var session = Session.Start(user.TenantId, user.Id, RefreshTokens.Hash(refreshToken), now).Value;
        sessions.Add(session);

        // All repositories share one DbContext per request, so this one save writes the user, factor and session together.
        await factors.SaveChangesAsync(ct);
        await auditor.SucceededAsync(user.TenantId, user.Id, AuthAuditor.SignIn, cmd.Ip, methods[1], ct);

        var accessToken = tokens.IssueAccessToken(new AccessTokenClaims(
            user.Id, user.TenantId, user.Role.ToString(),
            SessionId: session.Id,   // the access token now points at a real session
            Amr: methods,
            AuthTime: now,
            TokenVersion: user.TokenVersion), now);

        return new VerifyMfaResult(accessToken, refreshToken, session.ExpiresAt);
    }

    // A wrong code counts like a wrong password: 5 in a row lock the account for 15 minutes.
    private async Task<Result<VerifyMfaResult>> RejectAsync(User user, DateTimeOffset now, string reason, string? ip, CancellationToken ct)
    {
        user.RegisterFailedLogin(now);
        await users.SaveChangesAsync(ct);
        await auditor.FailedAsync(user, AuthAuditor.SignIn, reason, ip, ct);
        return ConfirmTotpHandler.InvalidCode;
    }
}