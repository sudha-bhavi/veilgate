using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.ApiClients;

namespace VeilGate.Api.Security;

/// <summary>
/// Runs right after authentication. Takes the tenant ONLY from the signed "tid" claim (never from the URL, a header or the body),
/// sets it once for the request, then checks the token is still valid: the user must exist in that tenant, be Active, and carry
/// the current token version. Changing a password or disabling a user therefore kills their tokens within one request.
/// </summary>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext ctx, ITenantContext tenant)
    {
        if (ctx.User.Identity?.IsAuthenticated == true && AuthPolicies.IsClientToken(ctx.User))
        {
            // An API client's token: the tenant comes from the signed tid claim as always. There is no user to look up.
            if (!Guid.TryParse(ctx.User.FindFirst("tid")?.Value, out var clientTenant) || clientTenant == Guid.Empty
                || !Guid.TryParse(ctx.User.FindFirst("client_id")?.Value, out var clientId))
            {
                await Reject(ctx);
                return;
            }
            tenant.Set(clientTenant);

            // A token lives up to 15 minutes, but revoking a client must work at once, and a client limited to certain networks
            // must not be usable from others with a token fetched elsewhere. So the client is checked on every call.
            // A client id with no row cannot be told apart from a forged one, and a forged token already fails the signature check,
            // so only a client that exists and is revoked, or is called from outside its allowlist, is turned away here.
            var state = await ctx.RequestServices.GetRequiredService<IApiClientRepository>().GetStateAsync(clientId, ctx.RequestAborted);
            if (state is not null && (state.Status != ApiClientStatus.Active
                || !ApiClient.IsIpAllowedBy(state.IpAllowlist, ctx.Connection.RemoteIpAddress?.ToString())))
            {
                await Reject(ctx);
                return;
            }
        }
        else if (ctx.User.Identity?.IsAuthenticated == true)
        {
            var ok = Guid.TryParse(ctx.User.FindFirst("tid")?.Value, out var tenantId)
                     & Guid.TryParse(ctx.User.FindFirst("sub")?.Value, out var userId)
                     & int.TryParse(ctx.User.FindFirst("ver")?.Value, out var version);
            if (!ok || tenantId == Guid.Empty)
            {
                await Reject(ctx);
                return;
            }

            tenant.Set(tenantId);
            var users = ctx.RequestServices.GetRequiredService<IUserRepository>(); // resolved only for signed-in requests
            var state = await users.GetTokenStateAsync(userId, ctx.RequestAborted);
            if (state is null || state.Status != UserStatus.Active || state.TokenVersion != version)
            {
                await Reject(ctx);
                return;
            }
        }
        await next(ctx);
    }

    private static async Task Reject(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        await ctx.Response.WriteAsJsonAsync(
            new Microsoft.AspNetCore.Mvc.ProblemDetails
            {
                Status = 401,
                Title = "Authentication required",
                Extensions = { ["code"] = "auth.token_invalid" }
            },
            options: null, contentType: "application/problem+json");
    }
}
