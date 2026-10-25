using FluentValidation;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

public sealed record LoginCommand(string Email, string Password);

/// <param name="MfaToken">Short-lived token to present at the MFA step. Not an access token.</param>
public sealed record LoginResult(bool MfaRequired, bool MfaEnrollmentRequired, string MfaToken);

public sealed class LoginValidator : AbstractValidator<LoginCommand>
{
    public LoginValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(254);
        RuleFor(x => x.Password).NotEmpty().MaximumLength(256); // upper bound stops giant inputs from burning hashing time
    }
}

/// <summary>
/// Step 1 of sign-in (Backend doc, POST /auth/login). Checks email and password and returns a token for the MFA step.
/// Every failure that could reveal whether an account exists returns the same error, and takes about the same time.
/// </summary>
public sealed class LoginHandler(
    IUserRepository users, IMfaFactorRepository factors, IPasswordHasher hasher, ITenantContext tenant,
    ITokenService tokens, IClock clock, IValidator<LoginCommand> validator)
{
    public static readonly Error InvalidCredentials = Error.Unauthorized("auth.invalid_credentials", "Email or password is incorrect");
    public static readonly Error AccountLocked = Error.Locked("auth.account_locked", "Account temporarily locked. Try again in 15 minutes");

    public async Task<Result<LoginResult>> HandleAsync(LoginCommand cmd, CancellationToken ct = default)
    {
        var check = await validator.ValidateAsync(cmd, ct);
        if (!check.IsValid) return Error.Validation("validation.failed", check.Errors[0].ErrorMessage);

        var email = cmd.Email.Trim().ToLowerInvariant();
        var candidate = await users.FindLoginCandidateAsync(email, ct);
        if (candidate is null)
        {
            hasher.VerifyDummy(cmd.Password);
            return InvalidCredentials;
        }

        // From here on the tenant is known, so row-level security applies to everything we read or write.
        tenant.Set(candidate.TenantId);
        var user = await users.GetCurrentTenantUserAsync(candidate.UserId, ct);
        if (user is null || user.Status != UserStatus.Active || !await users.IsTenantActiveAsync(ct))
        {
            hasher.VerifyDummy(cmd.Password);
            return InvalidCredentials;
        }

        var now = clock.UtcNow;
        if (user.IsLocked(now)) return AccountLocked;

        if (user.PasswordHash is null || !hasher.Verify(cmd.Password, user.PasswordHash))
        {
            user.RegisterFailedLogin(now);
            await users.SaveChangesAsync(ct);
            return InvalidCredentials;
        }

        user.RegisterSuccessfulLogin(now);
        await users.SaveChangesAsync(ct);

                // Has this user finished setting up MFA? If so they enter a code; if not they must set it up first.
        var factor = await factors.GetTotpForUserAsync(user.Id, ct);
        var hasConfirmedMfa = factor is { IsConfirmed: true };

        var mfaToken = tokens.IssueMfaChallengeToken(user.Id, user.TenantId, user.TokenVersion, now);
        return new LoginResult(MfaRequired: hasConfirmedMfa, MfaEnrollmentRequired: !hasConfirmedMfa, MfaToken: mfaToken);
    }
}
