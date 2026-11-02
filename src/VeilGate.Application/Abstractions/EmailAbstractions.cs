namespace VeilGate.Application.Abstractions;

/// <summary>A plain-text email. No HTML on purpose: nothing a person typed can ever be turned into markup.</summary>
public sealed record EmailMessage(string To, string Subject, string TextBody);

/// <summary>Sends email. The Application layer only knows this interface; the real sender (SMTP, MailHog in development) is in Infrastructure.</summary>
public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct);
}

/// <summary>Builds addresses of console pages for use in emails. The console's address is configuration, not code.</summary>
public interface IConsoleLinks
{
    /// <summary>The accept-invitation page for this invitation token.</summary>
    string AcceptInvitation(string token);
}