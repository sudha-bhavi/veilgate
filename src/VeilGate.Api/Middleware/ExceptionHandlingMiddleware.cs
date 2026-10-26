using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace VeilGate.Api.Middleware;

/// <summary>
/// Converts any unhandled exception into an RFC 9457 problem+json body with code server.error.
/// Never returns stack traces, SQL or type names (Backend doc 7.2). Details go to the server log only.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try { await next(ctx); }
        catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { ctx.Response.StatusCode = 499; }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge && !ctx.Response.HasStarted)
        {
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            var problem = new ProblemDetails { Status = 413, Title = "The request body is too large." };
            problem.Extensions["code"] = "payload.too_large";
            await ctx.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Unhandled exception for {Method} {Path}", ctx.Request.Method, ctx.Request.Path.Value);
            if (ctx.Response.HasStarted) throw;
            ctx.Response.Clear();
            ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
            ctx.Response.ContentType = "application/problem+json";
            var problem = new ProblemDetails
            {
                Type = "https://docs.veilgate.example/errors/server.error",
                Title = "Unexpected error",
                Status = 500
            };
            problem.Extensions["code"] = "server.error";
            problem.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.TraceIdentifier;
            await ctx.Response.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json");
        }
    }
}
