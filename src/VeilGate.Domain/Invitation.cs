using VeilGate.Domain.Common;

namespace VeilGate.Domain;

/// <summary>What <see cref="Invitation.Create"/> hands back: the row to save, and the raw token to put in the email link.</summary>
public sealed record NewInvitation(Invitation Invitation, string Token);

/// <summary>
/// An invitation to join a tenant (Database doc 2.5). Single use, valid for 48 hours. The link holds a random token;
/// we keep only its hash, so a copy of the database cannot be used to accept anyone's invitation.
/// </summary>
public sealed class Invitation : TenantEntity
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(48);

    private Invitation() { }

    public Guid UserId { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public UserRole Role { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public Guid InvitedBy { get; private set; }

    public static Result<NewInvitation> Create(Guid tenantId, Guid userId, string email, UserRole role, Guid invitedBy, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || userId == Guid.Empty || invitedBy == Guid.Empty)
            return Error.Validation("validation.failed", "Tenant, user and inviter are required.");

        // Same kind of token as a refresh token: 256 random bits, only the SHA-256 hash is stored.
        var token = RefreshTokens.Generate();
        var invitation = new Invitation
        {
            UserId = userId,
            Email = (email ?? string.Empty).Trim().ToLowerInvariant(),
            Role = role,
            TokenHash = RefreshTokens.Hash(token),
            ExpiresAt = now.Add(Lifetime),
            InvitedBy = invitedBy
        };
        invitation.SetTenant(tenantId);
        return new NewInvitation(invitation, token);
    }

    /// <summary>Not used yet, and not past its end time.</summary>
    public bool IsUsable(DateTimeOffset now) => AcceptedAt is null && ExpiresAt > now;

    /// <summary>Marks the invitation used. A second try, or a late one, gets the same answer, so a link reveals nothing about why it failed.</summary>
    public Result Accept(DateTimeOffset now)
    {
        if (!IsUsable(now)) return Result.Failure(Error.NotFound("invitation.invalid", "This link is no longer valid."));
        AcceptedAt = now;
        return Result.Success();
    }

    /// <summary>Kills the link without using it. Used when the invitation is resent, so the old email stops working.</summary>
    public void Revoke(DateTimeOffset now)
    {
        if (AcceptedAt is null && ExpiresAt > now) ExpiresAt = now;
    }
}