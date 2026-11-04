using System.Security.Cryptography;
using Npgsql;

namespace VeilGate.Api.Security;

/// <summary>
/// Outside Development, the API refuses to start with settings that are only fine on a developer's machine: the demo sign-in,
/// placeholder or weak secrets, plain-HTTP or localhost addresses, a database login that can do too much, the relaxed outbound
/// network rules, and the sign-in limit raised for penetration testing. It is the "refuse to start, loudly" half of the go-live
/// checklist (Deployment doc 6.5). The messages name the SETTING and the reason, never its value.
/// </summary>
public static class ProductionSafetyCheck
{
    /// <summary>The vault key committed in appsettings.Development.json. It is public, so it must never protect real data.</summary>
    private const string PublicDevelopmentKek = "rXdjiITfqa95aHntRn/eo93iNWw0Kz8mFMrTkgPsPn0=";

    private static readonly string[] WeakPasswordFragments = ["change-me", "changeme", "password", "postgres", "secret", "admin", "123456"];
    private static readonly string[] LocalHosts = ["localhost", "127.0.0.1", "::1", "[::1]", "mailhog", "host.docker.internal"];

    public const int MinimumPasswordLength = 16;
    public const int MaximumAccessTokenMinutes = 15;
    public const int MaximumLoginsPerMinute = 30;

    /// <summary>Throws, listing every problem, unless this is Development or nothing is wrong.</summary>
    public static void EnforceOrThrow(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment()) return;

        var problems = Problems(configuration);
        if (problems.Count == 0) return;

        throw new InvalidOperationException(
            $"VeilGate refuses to start in the '{environment.EnvironmentName}' environment with unsafe settings:{Environment.NewLine}"
            + string.Join(Environment.NewLine, problems.Select(p => " - " + p))
            + $"{Environment.NewLine}Fix the settings above. Their values are not shown on purpose.");
    }

    public static IReadOnlyList<string> Problems(IConfiguration c)
    {
        var problems = new List<string>();

        if (Flag(c, "Demo:Enabled") && !Flag(c, "Safety:AllowDemo"))
            problems.Add("Demo:Enabled is true. The demo signs visitors in without a password or a second factor, so it belongs only on a sandbox "
                         + "organisation with synthetic data. If that is what this is, also set Safety:AllowDemo=true.");

        CheckDatabase(c["ConnectionStrings:Postgres"], problems);
        CheckKey(c, "Security:VaultKek", problems);
        CheckKey(c, "Security:MfaEncryptionKey", problems);
        if (!string.IsNullOrEmpty(c["Security:VaultKek"]) && c["Security:VaultKek"] == c["Security:MfaEncryptionKey"])
            problems.Add("Security:VaultKek and Security:MfaEncryptionKey are the same value. Each key protects a different thing and must be different.");

        var origins = c.GetSection("Cors:AllowedOrigins").GetChildren().Select(o => o.Value).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (origins.Count == 0) problems.Add("Cors:AllowedOrigins is empty. List the exact https address of the console.");
        foreach (var origin in origins.Where(o => !IsPublicHttps(o!)))
            problems.Add("Cors:AllowedOrigins has an address that is not a public https address (plain http, localhost, or not an address).");

        if (!IsPublicHttps(c["Console:BaseUrl"]))
            problems.Add("Console:BaseUrl is not a public https address. Invitation emails would carry links to it.");

        var smtpHost = c["Smtp:Host"];
        if (string.IsNullOrWhiteSpace(smtpHost) || IsLocalHost(smtpHost))
            problems.Add("Smtp:Host is missing or points at this machine (a local mail catcher). Invitations would never reach anyone.");
        else if (!Flag(c, "Smtp:UseStartTls"))
            problems.Add("Smtp:UseStartTls is not true. Invitation links and credentials would cross the network unencrypted.");

        if (!Flag(c, "SecurityHeaders:Hsts"))
            problems.Add("SecurityHeaders:Hsts is not true. Serve the site over https and turn on Strict-Transport-Security.");

        var hosts = c["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Split(';').Any(h => h.Trim() == "*"))
            problems.Add("AllowedHosts is '*' or empty. List the real host name(s) so a forged Host header is refused.");

        if (Flag(c, "Llm:AllowPrivateNetworks"))
            problems.Add("Llm:AllowPrivateNetworks is true. That switches off the protection against calls to internal addresses (SSRF).");

        if (c.GetSection("Proxy:TrustedNetworks").GetChildren().Any(n => n.Value?.Trim().EndsWith("/0", StringComparison.Ordinal) == true))
            problems.Add("Proxy:TrustedNetworks trusts every address (a /0 network), so anyone could forge their IP address with an X-Forwarded-For header. "
                         + "List only the proxy's own network.");

        if (Int(c, "Auth:AccessTokenMinutes", 10) > MaximumAccessTokenMinutes)
            problems.Add($"Auth:AccessTokenMinutes is above {MaximumAccessTokenMinutes}. A stolen access token would stay useful for too long.");
        if (Int(c, "Auth:LoginRateLimitPerMinute", 10) > MaximumLoginsPerMinute)
            problems.Add($"Auth:LoginRateLimitPerMinute is above {MaximumLoginsPerMinute}. Raise it only for a penetration test, never in a deployment.");

        return problems;
    }

    private static void CheckDatabase(string? connectionString, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            problems.Add("ConnectionStrings:Postgres is missing.");
            return;
        }

        NpgsqlConnectionStringBuilder builder;
        try { builder = new NpgsqlConnectionStringBuilder(connectionString); }
        catch (ArgumentException)
        {
            problems.Add("ConnectionStrings:Postgres is not a valid connection string.");
            return;
        }

        if (!string.Equals(builder.Username, "vg_app", StringComparison.Ordinal))
            problems.Add("ConnectionStrings:Postgres must connect as the least-privilege role vg_app. Any other login can do more than the app needs "
                         + "and may skip row-level security.");

        var password = builder.Password ?? string.Empty;
        if (password.Length < MinimumPasswordLength || WeakPasswordFragments.Any(w => password.Contains(w, StringComparison.OrdinalIgnoreCase)))
            problems.Add($"The database password in ConnectionStrings:Postgres is shorter than {MinimumPasswordLength} characters or looks like a placeholder.");
    }

    private static void CheckKey(IConfiguration c, string name, List<string> problems)
    {
        var value = c[name];
        if (string.IsNullOrWhiteSpace(value))
        {
            problems.Add($"{name} is missing (32 random bytes, base64-encoded).");
            return;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(value); }
        catch (FormatException)
        {
            problems.Add($"{name} is not valid base64.");
            return;
        }

        if (bytes.Length != 32) problems.Add($"{name} must be exactly 32 bytes once decoded.");
        else if (bytes.Distinct().Count() < 8) problems.Add($"{name} does not look random (it repeats too few different bytes).");

        if (value == PublicDevelopmentKek) problems.Add($"{name} is the development key that is published in the repository. Generate a new one.");
    }

    private static bool Flag(IConfiguration c, string key) => bool.TryParse(c[key], out var value) && value;

    private static int Int(IConfiguration c, string key, int fallback) => int.TryParse(c[key], out var value) ? value : fallback;

    private static bool IsLocalHost(string host) => LocalHosts.Contains(host.Trim(), StringComparer.OrdinalIgnoreCase);

    private static bool IsPublicHttps(string? address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !IsLocalHost(uri.Host);
}
