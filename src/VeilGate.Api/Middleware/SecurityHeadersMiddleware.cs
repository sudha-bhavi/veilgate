namespace VeilGate.Api.Middleware;

/// <summary>
/// Headers that harden browsers (Security doc 4.3). The API answers JSON, so its policy forbids loading anything at all.
/// The console is served from the same origin (the refresh cookie is SameSite=Strict, so it must be), and its pages get a
/// policy that allows only our own files.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public const string ApiPolicy = "default-src 'none'; frame-ancestors 'none'";

    public static readonly string ConsolePolicy = string.Join("; ",
        "default-src 'self'",
        "script-src 'self'",                    // no inline scripts and no eval: the built index.html loads one module file
        "style-src 'self' 'unsafe-inline'",     // Monaco adds <style> elements and style attributes while it runs
        "img-src 'self' data:",                 // Monaco's stylesheet has small data: images
        "font-src 'self'",
        "connect-src 'self'",                   // the console only talks to its own API
        "worker-src 'self'",                    // Monaco's worker is a file in /assets, never a blob: URL
        "object-src 'none'",
        "base-uri 'none'",
        "form-action 'self'",
        "frame-ancestors 'none'");

    // Everything under these belongs to the API, not to the console.
    private static readonly string[] ApiPrefixes = ["/api", "/oauth", "/health", "/.well-known"];

    public static bool IsApiPath(PathString path) => ApiPrefixes.Any(prefix => path.StartsWithSegments(prefix));

    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            var path = ctx.Request.Path;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Cross-Origin-Opener-Policy"] = "same-origin";

            if (IsApiPath(path))
            {
                h["Content-Security-Policy"] = ApiPolicy;
                h["Cache-Control"] = "no-store";
            }
            else
            {
                h["Content-Security-Policy"] = ConsolePolicy;
                // Built files have a content hash in their name, so they never change; index.html must be checked on every load
                // so a new release is picked up.
                h["Cache-Control"] = path.StartsWithSegments("/assets") ? "public, max-age=31536000, immutable" : "no-cache";
            }
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}
