namespace VeilGate.Api.Middleware;

/// <summary>API responses are JSON only; these headers harden browsers that might load them (Security doc 4.3).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Cache-Control"] = "no-store";
            return Task.CompletedTask;
        });
        return next(ctx);
    }
}
