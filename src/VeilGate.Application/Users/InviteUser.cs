using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Users;

public sealed record InviteUserCommand(string Email, string Role, UserActor Actor);

/// <param name="EmailSent">False when the invitation was saved but the email could not be sent. The admin can use Resend.</param>
public sealed record InviteResult(UserView User, bool EmailSent, DateTimeOffset ExpiresAt);

/// <summary>Invites someone by email (Backend doc 5.1, POST /users). The account exists at once, with status Invited and no password.</summary>
public sealed class InviteUserHandler(
    IUserRepository users, IInvitationRepository invitations, ITenantContext tenant, IClock clock,
    InvitationMailer mailer, UserAuditor auditor)
{
    public static readonly Error EmailTaken = Error.Conflict("user.email_taken", "That email address already has an account.");

    public async Task<Result<InviteResult>> HandleAsync(InviteUserCommand cmd, CancellationToken ct = default)
    {
        if (!UserRoles.TryParseAssignable(cmd.Role, out var role))
            return Error.Validation("validation.failed", "Choose a role from the list.");

        // The person chooses their real name when they accept. Until then the name is the first part of the email address.
        var local = (cmd.Email ?? string.Empty).Trim().Split('@')[0];
        var placeholder = local.Length == 0 ? "Invited user" : local[..Math.Min(local.Length, 120)];

        var now = clock.UtcNow;
        var user = User.Invite(tenant.TenantId, cmd.Email ?? string.Empty, placeholder, role, now);
        if (user.IsFailure) return user.Error;

        // An email belongs to one account in the whole system (sign-in finds a person by email alone), so this checks every tenant.
        if (await users.FindLoginCandidateAsync(user.Value.Email, ct) is not null) return EmailTaken;

        var made = Invitation.Create(tenant.TenantId, user.Value.Id, user.Value.Email, role, cmd.Actor.UserId, now);
        if (made.IsFailure) return made.Error;

        users.Add(user.Value);
        invitations.Add(made.Value.Invitation);
        await users.SaveChangesAsync(ct);   // one DbContext, so the person and the invitation are saved together
        await auditor.WriteAsync(cmd.Actor, "user.invite", user.Value.Id, new Dictionary<string, string> { ["role"] = role.ToString() }, ct);

        var sent = await mailer.SendAsync(user.Value.Email, role, made.Value.Invitation.ExpiresAt, made.Value.Token, cmd.Actor.UserId, ct);
        return new InviteResult(UserView.From(user.Value, hasMfa: false), sent, made.Value.Invitation.ExpiresAt);
    }
}

public sealed record ResendInvitationCommand(Guid UserId, UserActor Actor);

/// <summary>Sends a fresh link to someone who has not accepted yet. Every earlier link stops working.</summary>
public sealed class ResendInvitationHandler(
    IUserRepository users, IInvitationRepository invitations, ITenantContext tenant, IClock clock,
    InvitationMailer mailer, UserAuditor auditor)
{
    public async Task<Result<InviteResult>> HandleAsync(ResendInvitationCommand cmd, CancellationToken ct = default)
    {
        var user = await users.GetCurrentTenantUserAsync(cmd.UserId, ct);
        if (user is null) return Error.NotFound();
        if (user.Status != UserStatus.Invited)
            return Error.Conflict("user.not_invited", "Only people who have not accepted yet can be sent a new invitation.");

        var now = clock.UtcNow;
        foreach (var old in await invitations.ListForUserAsync(user.Id, ct)) old.Revoke(now);

        var made = Invitation.Create(tenant.TenantId, user.Id, user.Email, user.Role, cmd.Actor.UserId, now);
        if (made.IsFailure) return made.Error;

        invitations.Add(made.Value.Invitation);
        await users.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "user.invite_resend", user.Id, null, ct);

        var sent = await mailer.SendAsync(user.Email, user.Role, made.Value.Invitation.ExpiresAt, made.Value.Token, cmd.Actor.UserId, ct);
        return new InviteResult(UserView.From(user, hasMfa: false), sent, made.Value.Invitation.ExpiresAt);
    }
}