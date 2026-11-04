namespace VeilGate.Api.Options;

public sealed class SecurityHeadersOptions
{
    public const string Section = "SecurityHeaders";

    /// <summary>
    /// Sends Strict-Transport-Security, which tells browsers to use HTTPS for this site and never fall back to HTTP.
    /// Off by default: it is meaningless over plain HTTP (local development) and hard to undo once a browser has remembered it.
    /// Turn it on in a deployment that is served over HTTPS. Environment variable form: SecurityHeaders__Hsts=true
    /// </summary>
    public bool Hsts { get; set; }

    /// <summary>How long a browser remembers to use HTTPS, in days (Security doc: a year).</summary>
    public int HstsMaxAgeDays { get; set; } = 365;
}
