using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Application.Auth;

namespace VeilGate.Api.Middleware;

/// <summary>
/// Makes a demo session read-only. Whoever signs in with "Try the demo" may look at every page their role can see and may try
/// the playground and a policy on sample text (neither stores anything), and may sign out. Every other change is refused with
/// 403 demo.read_only before it reaches a controller, so a visitor can never invite people, add providers, publish or edit anything.
/// A demo session is recognised by its sign-in method ("amr" claim), which a real sign-in can never carry.
/// </summary>
public sealed class DemoReadOnlyMiddleware(RequestDelegate next)
{
    private static readonly Regex PolicyTest =
        new(@"^/api/v1/policies/[0-9a-fA-F-]{36}/test/?$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (IsDemoSession(ctx) && !MayDo(ctx.Request))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = 403,
                    Title = "The demo is read-only.",
                    Extensions = { ["code"] = "demo.read_only" }
                },
                options: null, contentType: "application/problem+json");
            return;
        }

        await next(ctx);
    }

    private static bool IsDemoSession(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true && ctx.User.FindAll("amr").Any(c => c.Value == DemoSignInHandler.Method);

    private static bool MayDo(HttpRequest request)
    {
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method)) return true;
        if (!HttpMethods.IsPost(request.Method)) return false;

        var path = request.Path.Value ?? string.Empty;
        return path.Equals("/api/v1/playground/run", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/api/v1/auth/logout", StringComparison.OrdinalIgnoreCase)
            || PolicyTest.IsMatch(path);
    }
}
