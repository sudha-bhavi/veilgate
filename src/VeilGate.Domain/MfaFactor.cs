using VeilGate.Domain.Common;

namespace VeilGate.Domain;

public enum MfaFactorType { Totp }

/// <summary>One second-login-step a user has set up. Starts unconfirmed; becomes usable after the first valid code.</summary>
public sealed class MfaFactor : TenantEntity
{
    private MfaFactor() { }

    public Guid UserId { get; private set; }
    public MfaFactorType Type { get; private set; }

    // The TOTP secret, already encrypted by ISecretProtector. The domain never sees the raw secret.
    public string ProtectedSecret { get; private set; } = string.Empty;

    public bool IsConfirmed { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }

    // Newest 30-second window already accepted. -1 means none yet. Blocks replay.
    public long LastUsedCounter { get; private set; } = -1;

    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static Result<MfaFactor> EnrollTotp(Guid tenantId, Guid userId, string protectedSecret, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) return Error.Validation("validation.failed", "Tenant is required.");
        if (userId == Guid.Empty) return Error.Validation("validation.failed", "User is required.");
        if (string.IsNullOrWhiteSpace(protectedSecret)) return Error.Validation("validation.failed", "Secret is required.");

        var factor = new MfaFactor
        {
            UserId = userId,
            Type = MfaFactorType.Totp,
            ProtectedSecret = protectedSecret,
            UpdatedAt = now,
        };
        factor.SetTenant(tenantId);
        return factor;
    }

    /// <summary>The user typed their first valid code. From now on this factor is required at login.</summary>
    public Result Confirm(long counter, DateTimeOffset now)
    {
        if (IsConfirmed)
            return Result.Failure(Error.Conflict("mfa.already_confirmed", "This factor is already confirmed."));

        IsConfirmed = true;
        ConfirmedAt = now;
        LastUsedCounter = counter;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>A login code was accepted. Remember its window so it can't be used again.</summary>
    public Result RecordUse(long counter, DateTimeOffset now)
    {
        if (!IsConfirmed)
            return Result.Failure(Error.Forbidden("mfa.not_confirmed", "This factor has not been confirmed."));

        if (counter <= LastUsedCounter)
            return Result.Failure(Error.Unauthorized("mfa.code_reused", "This code was already used."));

        LastUsedCounter = counter;
        UpdatedAt = now;
        return Result.Success();
    }
}