using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Users;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Infrastructure.Persistence;
using Xunit;

namespace VeilGate.Domain.Tests;

public class InvitationHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string Ip = "203.0.113.5";

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IInvitationRepository _invitations = Substitute.For<IInvitationRepository>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IConsoleLinks _links = Substitute.For<IConsoleLinks>();
    private readonly IPasswordHasher _hasher = Substitute.For<IPasswordHasher>();
    private readonly ITokenService _tokens = Substitute.For<ITokenService>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();
    private readonly User _admin;

    public InvitationHandlerTests()
    {
        _clock.UtcNow.Returns(Now);
        _admin = User.Invite(TenantId, "admin@acme.test", "A. Rao", UserRole.TenantAdmin, Now).Value;
        _admin.AcceptInvitation("stored-hash", Now);

        _users.GetCurrentTenantUserAsync(_admin.Id, Arg.Any<CancellationToken>()).Returns(_admin);
        _users.GetTenantNameAsync(Arg.Any<CancellationToken>()).Returns("Acme Health");
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(true);
        _users.FindLoginCandidateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<LoginCandidate?>(null));
        _links.AcceptInvitation(Arg.Any<string>()).Returns(c => "http://localhost:5173/accept-invite?token=" + c.Arg<string>());
        _hasher.Hash(Arg.Any<string>()).Returns("hashed");
        _tokens.IssueMfaChallengeToken(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<DateTimeOffset?>()).Returns("mfa-token");
    }

    private UserActor Actor => new(_admin.Id, Ip);
    private UserAuditor Auditor() => new(_audit, _tenant, _clock, NullLogger<UserAuditor>.Instance);
    private InvitationMailer Mailer() => new(_users, _email, _links, NullLogger<InvitationMailer>.Instance);
    private InvitationFinder Finder() => new(_invitations, _tenant, _clock);

    private InviteUserHandler InviteHandler()
    {
        _tenant.Set(TenantId);   // in the real API the signed-in admin's token sets this
        return new(_users, _invitations, _tenant, _clock, Mailer(), Auditor());
    }

    private ResendInvitationHandler ResendHandler()
    {
        _tenant.Set(TenantId);
        return new(_users, _invitations, _tenant, _clock, Mailer(), Auditor());
    }

    // ---- invite ----

    [Fact]
    public async Task Inviting_saves_the_person_and_the_invitation_and_emails_the_link()
    {
        var r = await InviteHandler().HandleAsync(new InviteUserCommand(" New@Acme.Test ", "Developer", Actor));

        r.IsSuccess.Should().BeTrue();
        r.Value.EmailSent.Should().BeTrue();
        r.Value.User.Email.Should().Be("new@acme.test");
        r.Value.User.Status.Should().Be("Invited");
               r.Value.User.Name.Should().Be("New");
        _users.Received(1).Add(Arg.Is<User>(u => u.Email == "new@acme.test" && u.Role == UserRole.Developer && u.TenantId == TenantId));
        _invitations.Received(1).Add(Arg.Is<Invitation>(i => i.Email == "new@acme.test"));
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "new@acme.test" && m.TextBody.Contains("accept-invite?token=")
                && m.TextBody.Contains("A. Rao") && m.TextBody.Contains("Acme Health")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Inviting_is_audited_without_the_email_address()
    {
        await InviteHandler().HandleAsync(new InviteUserCommand("new@acme.test", "Auditor", Actor));

        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == "user.invite" && e.Result == "success" && e.Ip == Ip && e.ActorId == _admin.Id),
            Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(
            Arg.Is<AuditEvent>(e => e.Details.Values.Any(v => v.Contains('@'))), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("PlatformAdmin")]
    [InlineData("Owner")]
    [InlineData("1")]
    [InlineData("")]
    public async Task Only_the_four_tenant_roles_can_be_handed_out(string role)
    {
        var r = await InviteHandler().HandleAsync(new InviteUserCommand("new@acme.test", role, Actor));

        r.Error.Kind.Should().Be(ErrorKind.Validation);
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_email_that_has_an_account_anywhere_is_refused()
    {
        _users.FindLoginCandidateAsync("new@acme.test", Arg.Any<CancellationToken>()).Returns(new LoginCandidate(Guid.NewGuid(), Guid.NewGuid()));

        var r = await InviteHandler().HandleAsync(new InviteUserCommand("new@acme.test", "Developer", Actor));

        r.Error.Should().Be(InviteUserHandler.EmailTaken);
        _users.DidNotReceive().Add(Arg.Any<User>());
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_bad_email_is_a_validation_error_and_saves_nothing()
    {
        var r = await InviteHandler().HandleAsync(new InviteUserCommand("not-an-email", "Developer", Actor));

        r.Error.Kind.Should().Be(ErrorKind.Validation);
        _users.DidNotReceive().Add(Arg.Any<User>());
    }

    [Fact]
    public async Task If_the_email_cannot_be_sent_the_invitation_still_exists()
    {
        _email.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("smtp down")));

        var r = await InviteHandler().HandleAsync(new InviteUserCommand("new@acme.test", "Developer", Actor));

        r.IsSuccess.Should().BeTrue();
        r.Value.EmailSent.Should().BeFalse();
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ---- resend ----

    private (User user, Invitation old) GivenInvitedPerson()
    {
        var u = User.Invite(TenantId, "new@acme.test", "new", UserRole.Developer, Now).Value;
        var old = Invitation.Create(TenantId, u.Id, u.Email, UserRole.Developer, _admin.Id, Now).Value.Invitation;
        _users.GetCurrentTenantUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(u);
        _invitations.ListForUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(new List<Invitation> { old });
        return (u, old);
    }

    [Fact]
    public async Task Resending_switches_the_old_link_off_and_emails_a_new_one()
    {
        var (u, old) = GivenInvitedPerson();

        var r = await ResendHandler().HandleAsync(new ResendInvitationCommand(u.Id, Actor));

        r.IsSuccess.Should().BeTrue();
        old.IsUsable(Now).Should().BeFalse();
        _invitations.Received(1).Add(Arg.Is<Invitation>(i => i.UserId == u.Id && i.IsUsable(Now)));
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "new@acme.test"), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(Arg.Is<AuditEvent>(e => e.Action == "user.invite_resend"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resending_to_someone_who_already_accepted_is_a_conflict()
    {
        var (u, _) = GivenInvitedPerson();
        u.AcceptInvitation("stored-hash", Now);

        var r = await ResendHandler().HandleAsync(new ResendInvitationCommand(u.Id, Actor));

        r.Error.Code.Should().Be("user.not_invited");
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resending_to_an_unknown_person_is_not_found()
    {
        var r = await ResendHandler().HandleAsync(new ResendInvitationCommand(Guid.NewGuid(), Actor));

        r.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    // ---- view and accept (nobody is signed in, so the tenant is not set yet) ----

    private (User user, Invitation invitation, string token) GivenOpenInvitation()
    {
        var u = User.Invite(TenantId, "new@acme.test", "new", UserRole.Developer, Now).Value;
        var made = Invitation.Create(TenantId, u.Id, u.Email, UserRole.Developer, _admin.Id, Now).Value;
        var hash = RefreshTokens.Hash(made.Token);
        _invitations.FindTenantByTokenHashAsync(hash, Arg.Any<CancellationToken>()).Returns((Guid?)TenantId);
        _invitations.FindByTokenHashAsync(hash, Arg.Any<CancellationToken>()).Returns(made.Invitation);
        _users.GetCurrentTenantUserAsync(u.Id, Arg.Any<CancellationToken>()).Returns(u);
        return (u, made.Invitation, made.Token);
    }

    private ViewInvitationHandler View() => new(Finder(), _users);
    private AcceptInvitationHandler Accept() => new(Finder(), _users, _hasher, _tokens, _clock, Auditor());

    private static AcceptInvitationCommand Fill(string token, string name = "Jordan Reyes", string password = "correct horse battery staple")
        => new(token, name, password, Ip);

    [Fact]
    public async Task Viewing_shows_the_tenant_email_and_role_and_sets_the_tenant()
    {
        var (_, _, token) = GivenOpenInvitation();

        var r = await View().HandleAsync(token);

        r.IsSuccess.Should().BeTrue();
        r.Value.TenantName.Should().Be("Acme Health");
        r.Value.Email.Should().Be("new@acme.test");
        r.Value.Role.Should().Be("Developer");
        _tenant.TenantId.Should().Be(TenantId);
    }

    [Fact]
    public async Task An_unknown_token_is_invalid_and_no_tenant_is_set()
    {
        var r = await View().HandleAsync("no-such-token");

        r.Error.Should().Be(ViewInvitationHandler.Invalid);
        _tenant.IsSet.Should().BeFalse();
    }

    [Fact]
    public async Task Accepting_activates_the_person_and_hands_back_the_mfa_token()
    {
        var (user, invitation, token) = GivenOpenInvitation();

        var r = await Accept().HandleAsync(Fill(token));

        r.IsSuccess.Should().BeTrue();
        r.Value.MfaToken.Should().Be("mfa-token");
        user.Status.Should().Be(UserStatus.Active);
        user.DisplayName.Should().Be("Jordan Reyes");
        user.PasswordHash.Should().Be("hashed");
        invitation.AcceptedAt.Should().Be(Now);
        _hasher.Received(1).Hash("correct horse battery staple");
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == "user.invite_accept" && e.ActorId == user.Id && e.Ip == Ip), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_expired_link_is_invalid()
    {
        var (_, _, token) = GivenOpenInvitation();
        _clock.UtcNow.Returns(Now.AddHours(49));

        var r = await Accept().HandleAsync(Fill(token));

        r.Error.Should().Be(ViewInvitationHandler.Invalid);
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_weak_password_is_refused_and_the_link_stays_usable()
    {
        var (user, invitation, token) = GivenOpenInvitation();

        var r = await Accept().HandleAsync(Fill(token, password: "short"));

        r.Error.Code.Should().Be("password.weak");
        invitation.AcceptedAt.Should().BeNull();
        user.Status.Should().Be(UserStatus.Invited);
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_blank_name_is_refused()
    {
        var (_, _, token) = GivenOpenInvitation();

        var r = await Accept().HandleAsync(Fill(token, name: "   "));

        r.Error.Kind.Should().Be(ErrorKind.Validation);
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_person_who_is_already_active_cannot_use_the_link_again()
    {
        var (user, _, token) = GivenOpenInvitation();
        user.AcceptInvitation("stored-hash", Now);

        var r = await Accept().HandleAsync(Fill(token));

        r.Error.Should().Be(ViewInvitationHandler.Invalid);
    }

    [Fact]
    public async Task A_suspended_tenant_cannot_accept()
    {
        var (_, _, token) = GivenOpenInvitation();
        _users.IsTenantActiveAsync(Arg.Any<CancellationToken>()).Returns(false);

        var r = await Accept().HandleAsync(Fill(token));

        r.Error.Should().Be(ViewInvitationHandler.Invalid);
    }
}