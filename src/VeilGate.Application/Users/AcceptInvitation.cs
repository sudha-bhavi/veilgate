using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Users;

/// <summary>What the accept page shows before the person fills anything in.</summary>
public sealed record InvitationView(string TenantName, string Email, string Role, DateTimeOffset ExpiresAt);

/// <summary>GET /auth/invitations/{token}. Runs with nobody signed in.</summary>
public sealed class ViewInvitationHandler(InvitationFinder finder, IUserRepository users)
{
    public static readonly Error Invalid = Error.NotFound("invitation.invalid", "This link is no longer valid.");

    public async Task<Result<InvitationView>> HandleAsync(string? token, CancellationToken ct = default)
    {
        var invitation = await finder.FindUsableAsync(token, ct);
        if (invitation is null) return Invalid;

        var user = await users.GetCurrentTenantUserAsync(invitation.UserId, ct);
        if (user is null || user.Status != UserStatus.Invited) return Invalid;

        var tenantName = await users.GetTenantNameAsync(ct) ?? string.Empty;
        return new InvitationView(tenantName, invitation.Email, invitation.Role.ToString(), invitation.ExpiresAt);
    }
}

/// <param name="Ip">The caller's address, for the audit log only.</param>
public sealed record AcceptInvitationCommand(string? Token, string? FullName, string? Password, string? Ip = null);

/// <param name="MfaToken">The same short-lived token that sign-in hands out. It lets the person go straight on to set up two-step sign-in.</param>
public sealed record AcceptInvitationResult(string MfaToken);

/// <summary>POST /users/accept-invite. Sets the person's name and password, and uses up the link.</summary>
public sealed class AcceptInvitationHandler(
    InvitationFinder finder, IUserRepository users, IPasswordHasher hasher, ITokenService tokens, IClock clock, UserAuditor auditor)
{
    public async Task<Result<AcceptInvitationResult>> HandleAsync(AcceptInvitationCommand cmd, CancellationToken ct = default)
    {
        var invitation = await finder.FindUsableAsync(cmd.Token, ct);
        if (invitation is null) return ViewInvitationHandler.Invalid;

        var user = await users.GetCurrentTenantUserAsync(invitation.UserId, ct);
        if (user is null || user.Status != UserStatus.Invited || !await users.IsTenantActiveAsync(ct))
            return ViewInvitationHandler.Invalid;

        var weak = PasswordPolicy.Validate(cmd.Password, user.Email);
        if (weak.IsFailure) return weak.Error;

        var now = clock.UtcNow;
        var renamed = user.Rename(cmd.FullName ?? string.Empty, now);
        if (renamed.IsFailure) return renamed.Error;

        var accepted = user.AcceptInvitation(hasher.Hash(cmd.Password!), now);
        if (accepted.IsFailure) return accepted.Error;

        var used = invitation.Accept(now);
        if (used.IsFailure) return used.Error;

        await users.SaveChangesAsync(ct);   // the user and the invitation, together
        await auditor.WriteAsync(new UserActor(user.Id, cmd.Ip), "user.invite_accept", user.Id, null, ct);

        return new AcceptInvitationResult(tokens.IssueMfaChallengeToken(user.Id, user.TenantId, user.TokenVersion, now));
    }
}