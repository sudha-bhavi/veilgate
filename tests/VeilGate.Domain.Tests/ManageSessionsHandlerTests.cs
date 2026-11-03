using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Auth;
using VeilGate.Domain;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using Xunit;

namespace VeilGate.Domain.Tests;

public class ManageSessionsHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.NewGuid();
    private static readonly Guid Me = Guid.NewGuid();

    private readonly ISessionRepository _sessions = Substitute.For<ISessionRepository>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly ISecurityEventWriter _securityEvents = Substitute.For<ISecurityEventWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();

    public ManageSessionsHandlerTests() => _clock.UtcNow.Returns(Now);

    private AuthAuditor Auditor() => new(_audit, _securityEvents, _clock, NullLogger<AuthAuditor>.Instance);

    private static Session NewSession(Guid? user = null, DateTimeOffset? at = null, string? agent = null)
    {
        var hash = RefreshTokens.Hash(RefreshTokens.Generate());
        return Session.Start(Tenant, user ?? Me, hash, at ?? Now, ip: "203.0.113.5", userAgent: agent).Value;
    }

    // ---- list ----

    [Fact]
    public async Task Listing_marks_the_session_in_use_and_puts_the_most_recent_first()
    {
        var old = NewSession(at: Now.AddHours(-3), agent: "Safari");
        var current = NewSession(at: Now, agent: "Chrome");
        _sessions.ListActiveForUserAsync(Me, Now, Arg.Any<CancellationToken>()).Returns(new List<Session> { old, current });

        var rows = await new ListMySessionsHandler(_sessions, _clock).HandleAsync(Me, current.Id);

        rows.Select(r => r.Id).Should().Equal(current.Id, old.Id);
        rows[0].Current.Should().BeTrue();
        rows[1].Current.Should().BeFalse();
        rows[0].UserAgent.Should().Be("Chrome");
        rows[0].Ip.Should().Be("203.0.113.5");
    }

    // ---- revoke one ----

    private RevokeMySessionHandler Revoker() => new(_sessions, _clock, Auditor());

    [Fact]
    public async Task Revoking_another_session_of_mine_ends_it_and_is_audited()
    {
        var other = NewSession();
        _sessions.FindForUserAsync(other.Id, Me, Arg.Any<CancellationToken>()).Returns(other);

        var r = await Revoker().HandleAsync(new RevokeMySessionCommand(Tenant, Me, Guid.NewGuid(), other.Id, "203.0.113.9"));

        r.IsSuccess.Should().BeTrue();
        other.RevokedAt.Should().Be(Now);
        other.RevokeReason.Should().Be("revoked_by_user");
        await _sessions.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == "auth.session_revoke" && e.ActorId == Me && e.Details["count"] == "1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_session_in_use_cannot_be_revoked_here()
    {
        var current = NewSession();

        var r = await Revoker().HandleAsync(new RevokeMySessionCommand(Tenant, Me, current.Id, current.Id));

        r.Error.Should().Be(RevokeMySessionHandler.IsCurrent);
        current.RevokedAt.Should().BeNull();
        await _sessions.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_session_that_is_not_mine_is_not_found()
    {
        _sessions.FindForUserAsync(Arg.Any<Guid>(), Me, Arg.Any<CancellationToken>()).Returns((Session?)null);

        var r = await Revoker().HandleAsync(new RevokeMySessionCommand(Tenant, Me, Guid.NewGuid(), Guid.NewGuid()));

        r.IsFailure.Should().BeTrue();
        r.Error.Kind.Should().Be(ErrorKind.NotFound);
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_session_that_is_already_over_is_not_found()
    {
        var done = NewSession();
        done.Revoke("logout", Now.AddMinutes(-1));
        _sessions.FindForUserAsync(done.Id, Me, Arg.Any<CancellationToken>()).Returns(done);

        var r = await Revoker().HandleAsync(new RevokeMySessionCommand(Tenant, Me, Guid.NewGuid(), done.Id));

        r.Error.Kind.Should().Be(ErrorKind.NotFound);
    }

    // ---- sign out all others ----

    private LogoutAllHandler LogoutAll() => new(_sessions, _clock, Auditor());

    [Fact]
    public async Task Logout_all_keeping_the_current_session_ends_only_the_others()
    {
        var current = NewSession();
        var a = NewSession();
        var b = NewSession();
        _sessions.ListActiveForUserAsync(Me, Now, Arg.Any<CancellationToken>()).Returns(new List<Session> { current, a, b });

        var r = await LogoutAll().HandleAsync(new LogoutAllCommand(Tenant, Me, current.Id, KeepCurrent: true));

        r.Value.Should().Be(2);
        current.RevokedAt.Should().BeNull();
        a.RevokeReason.Should().Be("logout_all");
        b.RevokedAt.Should().Be(Now);
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEvent>(e => e.Action == "auth.logout_all" && e.Details["count"] == "2"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Logout_all_without_keeping_the_current_session_ends_every_one()
    {
        var current = NewSession();
        var a = NewSession();
        _sessions.ListActiveForUserAsync(Me, Now, Arg.Any<CancellationToken>()).Returns(new List<Session> { current, a });

        var r = await LogoutAll().HandleAsync(new LogoutAllCommand(Tenant, Me, current.Id, KeepCurrent: false));

        r.Value.Should().Be(2);
        current.RevokedAt.Should().Be(Now);
    }

    [Fact]
    public async Task Logout_all_with_nothing_to_end_saves_and_audits_nothing()
    {
        var current = NewSession();
        _sessions.ListActiveForUserAsync(Me, Now, Arg.Any<CancellationToken>()).Returns(new List<Session> { current });

        var r = await LogoutAll().HandleAsync(new LogoutAllCommand(Tenant, Me, current.Id, KeepCurrent: true));

        r.Value.Should().Be(0);
        await _sessions.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEvent>(), Arg.Any<CancellationToken>());
    }
}
