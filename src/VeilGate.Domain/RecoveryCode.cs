using VeilGate.Domain.Common;

namespace VeilGate.Domain;

/// <summary>One backup login code. We store only its hash, and each code works exactly once.</summary>
public sealed class RecoveryCode : TenantEntity
{
    private RecoveryCode() { }

    public Guid UserId { get; private set; }

    // SHA-256 hex from RecoveryCodes.Hash. The real code is shown to the user once and never stored.
    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset? UsedAt { get; private set; }
    public bool IsUsed => UsedAt is not null;

    public static Result<RecoveryCode> Create(Guid tenantId, Guid userId, string codeHash, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) return Error.Validation("validation.failed", "Tenant is required.");
        if (userId == Guid.Empty) return Error.Validation("validation.failed", "User is required.");
        if (codeHash is null || codeHash.Length != 64 || !codeHash.All(char.IsAsciiHexDigitLower))
            return Error.Validation("validation.failed", "Code hash must be 64 lowercase hex characters.");

        var code = new RecoveryCode { UserId = userId, CodeHash = codeHash };
        code.SetTenant(tenantId);
        return code;
    }

    /// <summary>Spends the code. A second attempt fails, so a leaked or reused code is useless.</summary>
    public Result MarkUsed(DateTimeOffset now)
    {
        if (IsUsed)
            return Result.Failure(Error.Conflict("mfa.recovery_code_used", "This recovery code was already used."));

        UsedAt = now;
        return Result.Success();
    }
}