using System.Globalization;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;

namespace VeilGate.Application.Users;

/// <summary>The text of the invitation email. One place, so the wording is easy to change and to test.</summary>
public static class InvitationEmail
{
    public static EmailMessage Build(string to, string tenantName, string inviterName, UserRole role, string link, DateTimeOffset expiresAt)
    {
        var expires = expiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var body = $"""
            {inviterName} invited you to join {tenantName} on VeilGate as {role}.

            To accept, open this link and choose a password:
            {link}

            The link works once and expires on {expires} UTC.
            If you were not expecting this, you can ignore this email.
            """;

        // The subject is fixed text. Names are typed by people, and a line break inside an email header would let them add headers of their own.
        return new EmailMessage(to, "You are invited to VeilGate", body);
    }
}