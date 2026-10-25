using FluentValidation;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Auth;

public sealed record ConfirmTotpCommand(Guid UserId, Guid TenantId, string Code);

/// <param name="RecoveryCodes">The readable backup codes. They exist in this response only; the database keeps just their hashes.</param>
public sealed record ConfirmTotpResult(IReadOnlyList<string> RecoveryCodes);

public sealed class ConfirmTotpValidator : AbstractValidator<ConfirmTotpCommand>
{
    public ConfirmTotpValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(12);
    }
}

public sealed class ConfirmTotpHandler(
    IUserRepository users, IMfaFactorRepository factors, IRecoveryCodeRepository recoveryCodeStore,
    ITenantContext tenant, ISecretProtector protector, IClock clock, IValidator<ConfirmTotpCommand> validator)
{
    public static readonly Error InvalidCode = Error.Unauthorized("mfa.code_invalid", "The code is incorrect or expired.");

    public async Task<Result<ConfirmTotpResult>> HandleAsync(ConfirmTotpCommand cmd, CancellationToken ct = default)
    {
        var check = await validator.ValidateAsync(cmd, ct);
        if (!check.IsValid) return Error.Validation("validation.failed", check.Errors[0].ErrorMessage);

        if (!tenant.IsSet) tenant.Set(cmd.TenantId);

        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null || user.Status != UserStatus.Active)
            return Error.Unauthorized("auth.invalid_token", "Sign in again.");

        var factor = await factors.GetTotpForUserAsync(user.Id, ct);
        if (factor is null)
            return Error.NotFound("mfa.not_enrolled", "Start MFA setup first.");
        if (factor.IsConfirmed)
            return Error.Conflict("mfa.already_confirmed", "MFA is already confirmed.");

        // Decrypt the secret, then check the typed code against it.
        var secret = protector.Unprotect(factor.ProtectedSecret, StartTotpEnrollmentHandler.ProtectionContext(user.Id));
        var now = clock.UtcNow;
        var window = Totp.Verify(secret, cmd.Code, now.ToUnixTimeSeconds());
        if (window is null) return InvalidCode;

        var confirmed = factor.Confirm(window.Value, now);
        if (confirmed.IsFailure) return confirmed.Error;

        // Backup codes: shown to the user once, right now. Only their hashes are stored.
        var readable = RecoveryCodes.Generate();
        var entities = new List<RecoveryCode>(readable.Count);
        foreach (var code in readable)
        {
            var made = RecoveryCode.Create(user.TenantId, user.Id, RecoveryCodes.Hash(code), now);
            if (made.IsFailure) return made.Error;
            entities.Add(made.Value);
        }
        recoveryCodeStore.AddRange(entities);

        // One save: the confirmation and the codes are stored together, or not at all.
        await factors.SaveChangesAsync(ct);
        return new ConfirmTotpResult(readable);
    }
}