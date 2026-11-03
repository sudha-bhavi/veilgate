using FluentAssertions;
using VeilGate.Application.Abstractions;
using Xunit;

namespace VeilGate.Domain.Tests;

public class SessionStateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_session_that_is_not_revoked_and_not_expired_stands()
        => new SessionState(null, Now.AddDays(1)).IsEnded(Now).Should().BeFalse();

    [Fact]
    public void A_revoked_session_is_ended_however_much_time_is_left()
        => new SessionState(Now.AddMinutes(-1), Now.AddDays(1)).IsEnded(Now).Should().BeTrue();

    [Fact]
    public void A_session_is_ended_from_the_moment_of_its_hard_limit()
    {
        var limit = Now.AddHours(1);

        new SessionState(null, limit).IsEnded(limit.AddTicks(-1)).Should().BeFalse();
        new SessionState(null, limit).IsEnded(limit).Should().BeTrue();
        new SessionState(null, limit).IsEnded(limit.AddDays(1)).Should().BeTrue();
    }
}
