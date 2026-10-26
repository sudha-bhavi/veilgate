namespace VeilGate.Api.Security;

/// <summary>The refresh token travels only in this cookie, never in JSON, so page scripts (and XSS) can't read it.</summary>
public static class RefreshCookie
{
    public const string Name = "vg_refresh";
    private const string CookiePath = "/api/v1/auth";   // the browser sends it only to auth endpoints, not to every API call

    public static void Write(HttpResponse response, string token, DateTimeOffset expiresAt) =>
        response.Cookies.Append(Name, token, Options(expiresAt));

    public static string? Read(HttpRequest request) => request.Cookies[Name];

    public static void Clear(HttpResponse response) => response.Cookies.Delete(Name, Options(null));

    private static CookieOptions Options(DateTimeOffset? expiresAt) => new()
    {
        HttpOnly = true,                  // JavaScript can't read it
        Secure = true,                    // only sent over HTTPS
        SameSite = SameSiteMode.Strict,   // never sent from another site's page, which blocks CSRF
        Path = CookiePath,
        Expires = expiresAt
    };
}