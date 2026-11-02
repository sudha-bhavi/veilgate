using FluentAssertions;
using VeilGate.Application.Users;
using VeilGate.Domain;
using Xunit;

namespace VeilGate.Domain.Tests;

public class InvitationEmailTests
{
    private static readonly DateTimeOffset Expires = new(2026, 11, 4, 12, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_body_has_the_link_the_role_and_the_expiry()
    {
        var mail = InvitationEmail.Build("new@acme.test", "Acme Health", "A. Rao", UserRole.Developer,
            "http://localhost:5173/accept-invite?token=abc", Expires);

        mail.To.Should().Be("new@acme.test");
        mail.TextBody.Should().Contain("A. Rao invited you to join Acme Health on VeilGate as Developer.");
        mail.TextBody.Should().Contain("http://localhost:5173/accept-invite?token=abc");
        mail.TextBody.Should().Contain("2026-11-04 12:30 UTC");
    }

    [Fact]
    public void Names_never_reach_the_subject()
    {
        var mail = InvitationEmail.Build("new@acme.test", "Acme\r\nBcc: evil@x.test", "A. Rao\r\nBcc: evil@x.test", UserRole.Auditor, "http://x/y", Expires);

        mail.Subject.Should().Be("You are invited to VeilGate");
    }
}