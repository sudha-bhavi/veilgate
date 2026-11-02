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

public class ManageUsersHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.NewGuid();
    private const string Ip = "203.0.113.5";

    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IMfaFactorRepository _factors = Substitute.For<IMfaFactorRepository>();
    private readonly IRecoveryCodeRepository _recoveryCodes = Substitute.For<IRecoveryCodeRepository>();
    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly TenantContext _tenant = new();
    private readonly User _admin;
    private readonly User _target;

    public ManageUsersHandlerTests()
    {
        _tenant.Set(TenantId);
        _clock.UtcNow.Returns(Now);

        _admin = User.Invite(TenantId, "admin@acme.test", "A. Rao", UserRole.TenantAdmin, Now).Value;
        _admin.AcceptInvitation("stored-hash", Now);
        _target = User.Invite(TenantId, "ann@acme.test", "Ann Lee", UserRole.Developer, Now).Value;
        _target.AcceptInvitation("stored-hash", Now);

        _users.GetCurrentTenantUserAsync(_target.Id, Arg.Any<CancellationToken>()).Returns(_target);
        _users.CountActiveAdminsExceptAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(1);
        // Empty lists, not null: the handlers loop over them.
        _sessions.ListActiveForUserAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()).Returns(new List<Session>());
        _recoveryCodes.ListForUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<RecoveryCode>());
    }

    private UserActor Actor => new(_admin.Id, Ip);
    private UserAuditor Auditor() => new(_audit, _tenant, _clock, NullLogger<UserAuditor>.Instance);
    private ChangeUserRoleHandler ChangeRole() => new(_users, _factors, _clock, Auditor());
    private DisableUserHandler Disable() => new(_users, _factors, _sessions, _clock, Auditor());
    private EnableUserHandler Enable() => new(_users, _factors, _clock, Auditor());
    private ResetMfaHandler Reset() => new(_users, _factors, _recoveryCodes, _sessions, _securityEvents, _clock, Auditor(), NullLogger<ResetMfaHandler>.Instance);

    private Session GivenOneSession()
    {
        var s = Session.Start(TenantId, _target.Id, RefreshTokens.Hash(RefreshTokens.Generate()), Now).Value;
        _sessions.ListActiveForUserAsync(_target.Id, Now, Arg.Any<CancellationToken>()).Returns(new List<Session> { s });
        return s;
    }

    private Task AuditedAsync(string action) =>
        _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == action && e.Result == "success" && e.Ip == Ip && e.ActorId == _admin.Id && e.ResourceId == _target.Id),
            Arg.Any<CancellationToken>());

    // ---- list ----

    [Fact]
    public async Task Listing_passes_the_filters_on_and_shows_who_has_mfa()
    {
        IReadOnlyList<UserListRow> rows = new List<UserListRow> { new(_target, true) };
        _users.ListAsync(UserRole.Developer, UserStatus.Active, "ann", 2, 10, Arg.Any<CancellationToken>()).Returns((rows, 11));

        var r = await new ListUsersHandler(_users).HandleAsync("Developer", "Active", "ann", 2, 10);

        r.IsSuccess.Should().BeTrue();
        r.Value.Total.Should().Be(11);
        var only = r.Value.Items.Single();
        only.Email.Should().Be("ann@acme.test");
        only.HasMfa.Should().BeTrue();
    }

    [Fact]
    public async Task Listing_keeps_the_page_numbers_in_range()
    {
        IReadOnlyList<UserListRow> none = new List<UserListRow>();
        _users.ListAsync(null, null, null, Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((none, 0));

        await new ListUsersHandler(_users).HandleAsync(null, null, null, 0, 1000);

        await _users.Received(1).ListAsync(null, null, null, 1, 100, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Owner", null)]
    [InlineData("3", null)]
    [InlineData(null, "Gone")]
    public async Task Listing_refuses_unknown_roles_and_statuses(string? role, string? status)
    {
        var r = await new ListUsersHandler(_users).HandleAsync(role, status, null, 1, 25);

        r.Error.Kind.Should().Be(ErrorKind.Validation);
    }

    // ---- change role ----

    [Fact]
    public async Task Changing_the_role_saves_bumps_the_token_version_and_is_audited_with_the_new_role()
    {
        var r = await ChangeRole().HandleAsync(new ChangeUserRoleCommand(_target.Id, "Auditor", Actor));

        r.IsSuccess.Should().BeTrue();
        r.Value.Role.Should().Be("Auditor");
        _target.Role.Should().Be(UserRole.Auditor);
        _target.TokenVersion.Should().Be(2);
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await AuditedAsync("user.role_change");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Details["role"] == "Auditor"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task You_cannot_change_your_own_role()
    {
        var r = await ChangeRole().HandleAsync(new ChangeUserRoleCommand(_admin.Id, "Developer", Actor));

        r.Error.Should().Be(UserRules.SelfChange);
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("PlatformAdmin")]
    [InlineData("Owner")]
    public async Task Only_the_four_tenant_roles_can_be_chosen(string role)
    {
        var r = await ChangeRole().HandleAsync(new ChangeUserRoleCommand(_target.Id, role, Actor));

        r.Error.Kind.Should().Be(ErrorKind.Validation);
        _target.Role.Should().Be(UserRole.Developer);
    }

    [Fact]
    public async Task Changing_the_role_of_an_unknown_or_other_tenants_user_is_not_found()
    {
        var r = await ChangeRole().HandleAsync(new ChangeUserRoleCommand(Guid.NewGuid(), "Auditor", Actor));

        r.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    [Fact]
    public async Task The_last_active_tenant_admin_cannot_be_demoted()
    {
        var onlyAdmin = User.Invite(TenantId, "boss@acme.test", "Boss", UserRole.TenantAdmin, Now).Value;
        onlyAdmin.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(onlyAdmin.Id, Arg.Any<CancellationToken>()).Returns(onlyAdmin);
        _users.CountActiveAdminsExceptAsync(onlyAdmin.Id, Arg.Any<CancellationToken>()).Returns(0);

        var r = await ChangeRole().HandleAsync(new ChangeUserRoleCommand(onlyAdmin.Id, "Developer", Actor));

        r.Error.Should().Be(UserRules.LastAdmin);
        onlyAdmin.Role.Should().Be(UserRole.TenantAdmin);
    }

    [Fact]
    public async Task An_admin_can_be_demoted_when_another_admin_remains()
    {
        var other = User.Invite(TenantId, "boss@acme.test", "Boss", UserRole.TenantAdmin, Now).Value;
        other.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(other.Id, Arg.Any<CancellationToken>()).Returns(other);

        var r = await ChangeRole().HandleAsync(new ChangeUserRoleCommand(other.Id, "Developer", Actor));

        r.IsSuccess.Should().BeTrue();
    }

    // ---- disable / enable ----

    [Fact]
    public async Task Disabling_signs_the_person_out_everywhere_and_is_audited()
    {
        var session = GivenOneSession();

        var r = await Disable().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.IsSuccess.Should().BeTrue();
        r.Value.Status.Should().Be("Disabled");
        _target.TokenVersion.Should().Be(2);
        session.RevokedAt.Should().Be(Now);
        session.RevokeReason.Should().Be("user_disabled");
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await AuditedAsync("user.disable");
    }

    [Fact]
    public async Task You_cannot_disable_yourself()
    {
        var r = await Disable().HandleAsync(new UserActionCommand(_admin.Id, Actor));

        r.Error.Should().Be(UserRules.SelfChange);
    }

    [Fact]
    public async Task The_last_active_tenant_admin_cannot_be_disabled()
    {
        var onlyAdmin = User.Invite(TenantId, "boss@acme.test", "Boss", UserRole.TenantAdmin, Now).Value;
        onlyAdmin.AcceptInvitation("stored-hash", Now);
        _users.GetCurrentTenantUserAsync(onlyAdmin.Id, Arg.Any<CancellationToken>()).Returns(onlyAdmin);
        _users.CountActiveAdminsExceptAsync(onlyAdmin.Id, Arg.Any<CancellationToken>()).Returns(0);

        var r = await Disable().HandleAsync(new UserActionCommand(onlyAdmin.Id, Actor));

        r.Error.Should().Be(UserRules.LastAdmin);
        onlyAdmin.Status.Should().Be(UserStatus.Active);
    }

    [Fact]
    public async Task Disabling_someone_already_disabled_is_a_conflict()
    {
        _target.Disable(Now);

        var r = await Disable().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    [Fact]
    public async Task Enabling_returns_the_person_to_active_and_is_audited()
    {
        _target.Disable(Now);

        var r = await Enable().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.IsSuccess.Should().BeTrue();
        r.Value.Status.Should().Be("Active");
        await AuditedAsync("user.enable");
    }

    [Fact]
    public async Task Enabling_someone_who_is_not_disabled_is_a_conflict()
    {
        var r = await Enable().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.Error.Kind.Should().Be(ErrorKind.Conflict);
    }

    // ---- reset two-step sign-in ----

    [Fact]
    public async Task Resetting_removes_the_authenticator_and_codes_and_ends_every_session()
    {
        var session = GivenOneSession();
        var factor = MfaFactor.EnrollTotp(TenantId, _target.Id, "v1:x", Now).Value;
        factor.Confirm(1, Now);
        _factors.GetTotpForUserAsync(_target.Id, Arg.Any<CancellationToken>()).Returns(factor);
        var code = RecoveryCode.Create(TenantId, _target.Id, RecoveryCodes.Hash(RecoveryCodes.Generate()[0]), Now).Value;
        _recoveryCodes.ListForUserAsync(_target.Id, Arg.Any<CancellationToken>()).Returns(new List<RecoveryCode> { code });

        var r = await Reset().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.IsSuccess.Should().BeTrue();
        _factors.Received(1).Remove(factor);
        _recoveryCodes.Received(1).RemoveRange(Arg.Is<IEnumerable<RecoveryCode>>(c => c.Single() == code));
        _target.TokenVersion.Should().Be(2);
        session.RevokeReason.Should().Be("mfa_reset");
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await AuditedAsync("user.mfa_reset");
    }

    [Fact]
    public async Task Resetting_raises_a_security_event_for_the_person()
    {
        await Reset().HandleAsync(new UserActionCommand(_target.Id, Actor));

        await _securityEvents.Received(1).WriteAsync(
            Arg.Is<SecurityEvent>(e => e.Type == "mfa.reset" && e.UserId == _target.Id && e.Severity == Severity.Medium && e.Details["reason"] == "admin_reset"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resetting_someone_with_no_authenticator_still_works()
    {
        var r = await Reset().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.IsSuccess.Should().BeTrue();
        _factors.DidNotReceive().Remove(Arg.Any<MfaFactor>());
    }

    [Fact]
    public async Task You_cannot_reset_your_own_two_step_sign_in()
    {
        var r = await Reset().HandleAsync(new UserActionCommand(_admin.Id, Actor));

        r.Error.Should().Be(UserRules.SelfChange);
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resetting_someone_who_is_not_active_is_a_conflict()
    {
        _target.Disable(Now);

        var r = await Reset().HandleAsync(new UserActionCommand(_target.Id, Actor));

        r.Error.Code.Should().Be("user.not_active");
        await _users.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Resetting_an_unknown_person_is_not_found()
    {
        var r = await Reset().HandleAsync(new UserActionCommand(Guid.NewGuid(), Actor));

        r.Error.Kind.Should().Be(ErrorKind.NotFound);
    }
}
