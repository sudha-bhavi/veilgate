namespace VeilGate.Infrastructure.Email;

/// <summary>Where to send email. Host has no default on purpose: a missing setting should fail loudly, not quietly try localhost in production.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string From { get; set; } = "no-reply@veilgate.test";
    public string FromName { get; set; } = "VeilGate";
    public bool UseStartTls { get; set; }
    public string? User { get; set; }
    public string? Password { get; set; }
}

/// <summary>The public address of the console, used to build links in emails (for example http://localhost:5173).</summary>
public sealed class ConsoleOptions
{
    public const string Section = "Console";

    public string BaseUrl { get; set; } = string.Empty;
}