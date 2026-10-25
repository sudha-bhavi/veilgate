using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

public sealed record StartTotpEnrollmentCommand(Guid UserId, Guid TenantId);

/// <param name="OtpAuthUri">Goes inside the QR code.</param>
/// <param name="ManualEntryKey">The same secret as typed text, for people who can't scan.</param>
public sealed record StartTotpEnrollmentResult(string OtpAuthUri, string ManualEntryKey);

public sealed class StartTotpEnrollmentHandler(
    IUserRepository users, IMfaFactorRepository factors, ITenantContext tenant,
    ISecretProtector protector, IClock clock)
{
    public const string Issuer = "VeilGate";

    /// <summary>Binds an encrypted secret to its owner, so it can't be copied to another user's row.</summary>
    public static string ProtectionContext(Guid userId) => $"mfa:{userId}";

    public async Task<Result<StartTotpEnrollmentResult>> HandleAsync(StartTotpEnrollmentCommand cmd, CancellationToken ct = default)
    {
        if (!tenant.IsSet) tenant.Set(cmd.TenantId);

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
            return Error.Unauthorized("auth.invalid_token", "Sign in again.");

        var existing = await factors.GetTotpForUserAsync(user.Id, ct);
        if (existing is { IsConfirmed: true })
            return Error.Conflict("mfa.already_enrolled", "MFA is already set up.");

        if (existing is not null)
        {
            // An earlier setup was abandoned. Remove it and save first: the table allows one TOTP
            // factor per user, so the old row must be gone before the new one is inserted.
            factors.Remove(existing);
            await factors.SaveChangesAsync(ct);
        }

        var secret = Totp.GenerateSecret();
        var enrolled = MfaFactor.EnrollTotp(
            user.TenantId, user.Id, protector.Protect(secret, ProtectionContext(user.Id)), clock.UtcNow);
        if (enrolled.IsFailure) return enrolled.Error;

        factors.Add(enrolled.Value);
        await factors.SaveChangesAsync(ct);

        return new StartTotpEnrollmentResult(Totp.BuildUri(Issuer, user.Email, secret), Base32.Encode(secret));
    }
}