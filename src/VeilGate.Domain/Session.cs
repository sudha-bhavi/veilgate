using System.Security.Cryptography;
using System.Text;
using VeilGate.Domain.Common;

namespace VeilGate.Domain;

/// <summary>One signed-in device. Holds the hash of its current refresh token and detects when an old one is replayed.</summary>
public sealed class Session : TenantEntity
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromHours(8);
    public static readonly TimeSpan AbsoluteTimeout = TimeSpan.FromDays(7);

    private Session() { }

    public Guid UserId { get; private set; }
    public string CurrentRefreshHash { get; private set; } = string.Empty;
    public string? PreviousRefreshHash { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }   // the hard limit, however active the user is
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevokeReason { get; private set; }

    public static readonly Error RefreshInvalid = Error.Unauthorized("auth.refresh_invalid", "Sign in again.");
    public static readonly Error RefreshReused = Error.Unauthorized("auth.refresh_reused", "Sign in again.");

    public static Result<Session> Start(Guid tenantId, Guid userId, string refreshHash, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) return Error.Validation("validation.failed", "Tenant is required.");
        if (userId == Guid.Empty) return Error.Validation("validation.failed", "User is required.");
        if (!IsHash(refreshHash)) return Error.Validation("validation.failed", "Refresh token hash is not valid.");

        var s = new Session
        {
            UserId = userId,
            CurrentRefreshHash = refreshHash,
            LastSeenAt = now,
            ExpiresAt = now + AbsoluteTimeout
        };
        s.SetTenant(tenantId);
        return s;
    }

    public bool IsActive(DateTimeOffset now) =>
        RevokedAt is null && now < ExpiresAt && now < LastSeenAt + IdleTimeout;

    /// <summary>
    /// Exchanges the presented refresh token for a new one. NOTE: when this returns the "reused" failure the session has
    /// just been revoked in memory, so the caller must still SAVE it, or the revocation is lost.
    /// </summary>
    public Result Rotate(string presentedHash, string newHash, DateTimeOffset now)
    {
        if (!IsHash(newHash)) return Result.Failure(Error.Validation("validation.failed", "Refresh token hash is not valid."));
        if (!IsActive(now)) return Result.Failure(RefreshInvalid);

        if (SameHash(presentedHash, CurrentRefreshHash))
        {
            PreviousRefreshHash = CurrentRefreshHash;
            CurrentRefreshHash = newHash;
            LastSeenAt = now;
            return Result.Success();
        }

        if (PreviousRefreshHash is not null && SameHash(presentedHash, PreviousRefreshHash))
        {
            // An old token came back. Someone has a copy, so end the whole session.
            Revoke("refresh_reuse", now);
            return Result.Failure(RefreshReused);
        }

        return Result.Failure(RefreshInvalid);
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is not null) return;   // keep the first reason
        RevokedAt = now;
        RevokeReason = reason;
    }

    private static bool IsHash(string? h) => h is { Length: 64 } && h.All(char.IsAsciiHexDigitLower);

    private static bool SameHash(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}