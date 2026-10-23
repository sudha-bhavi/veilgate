using VeilGate.Domain.Common;

namespace VeilGate.Domain;

public enum UserRole { PlatformAdmin, TenantAdmin, SecurityOfficer, Developer, Auditor }
public enum UserStatus { Invited, Active, Disabled, Locked }

/// <summary>A person who signs in to the console. Belongs to exactly one tenant (Database doc 2.2).</summary>
public sealed class User : TenantEntity
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    private User() { }

    public string Email { get; private set; } = string.Empty;
    public string DisplayName { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public UserStatus Status { get; private set; } = UserStatus.Invited;
    public string? PasswordHash { get; private set; }
    public DateTimeOffset? PasswordChangedAt { get; private set; }
    public short FailedAttempts { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public int TokenVersion { get; private set; } = 1;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static Result<User> Invite(Guid tenantId, string email, string displayName, UserRole role, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) return Error.Validation("validation.failed", "Tenant is required.");
        email = (email ?? string.Empty).Trim().ToLowerInvariant();
        displayName = (displayName ?? string.Empty).Trim();
        if (!IsPlausibleEmail(email)) return Error.Validation("validation.failed", "Email address is not valid.");
        if (displayName.Length is < 1 or > 120) return Error.Validation("validation.failed", "Name must be 1 to 120 characters.");

        var u = new User { Email = email, DisplayName = displayName, Role = role, UpdatedAt = now };
        u.SetTenant(tenantId);
        return u;
    }

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    /// <summary>Called when an invited person sets a password. Only valid once.</summary>
    public Result AcceptInvitation(string passwordHash, DateTimeOffset now)
    {
        if (Status != UserStatus.Invited) return Result.Failure(Error.Conflict("resource.conflict", "Invitation was already used."));
        if (string.IsNullOrWhiteSpace(passwordHash)) return Result.Failure(Error.Validation("validation.failed", "Password hash is required."));
        PasswordHash = passwordHash;
        PasswordChangedAt = now;
        Status = UserStatus.Active;
        UpdatedAt = now;
        return Result.Success();
    }

    public void RegisterFailedLogin(DateTimeOffset now)
    {
        FailedAttempts++;
        if (FailedAttempts >= MaxFailedAttempts)
        {
            LockedUntil = now.Add(LockDuration);
            FailedAttempts = 0;
        }
        UpdatedAt = now;
    }

    public void RegisterSuccessfulLogin(DateTimeOffset now)
    {
        FailedAttempts = 0;
        LockedUntil = null;
        LastLoginAt = now;
        UpdatedAt = now;
    }

    /// <summary>Bump when the password changes or MFA is reset, so every existing token stops working.</summary>
    public void InvalidateTokens(DateTimeOffset now)
    {
        TokenVersion++;
        UpdatedAt = now;
    }

    public Result Disable(DateTimeOffset now)
    {
        if (Status == UserStatus.Disabled) return Result.Failure(Error.Conflict("resource.conflict", "User is already disabled."));
        Status = UserStatus.Disabled;
        TokenVersion++;
        UpdatedAt = now;
        return Result.Success();
    }

    private static bool IsPlausibleEmail(string e)
    {
        if (e.Length is < 3 or > 254) return false;
        var at = e.IndexOf('@');
        return at > 0 && at == e.LastIndexOf('@') && at < e.Length - 3 && e.IndexOf('.', at) > at + 1 && !e.Contains(' ');
    }
}
