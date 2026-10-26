using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using VeilGate.Application.Gateway;

namespace VeilGate.Api.Security;

public static class GatewayScopes
{
    public const string Invoke = "gateway:invoke";
    public const string Detect = "gateway:detect";
    public const string Deidentify = "gateway:deidentify";
    public const string Reidentify = "gateway:reidentify";

    public static readonly string[] All = [Invoke, Detect, Deidentify, Reidentify];

    public static string PolicyName(string scope) => $"Scope:{scope}";
}

public static class AuthPolicies
{
    /// <summary>A signed-in person with a role that may use the console playground.</summary>
    public const string Playground = "Playground";

    public static bool IsClientToken(ClaimsPrincipal user) => user.HasClaim(c => c.Type == "client_id");

    public static bool HasScope(ClaimsPrincipal user, string scope) =>
        (user.FindFirst("scope")?.Value ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(scope, StringComparer.Ordinal);

    public static void AddVeilGatePolicies(this AuthorizationOptions options)
    {
        // The default for every endpoint is "a signed-in person". A client token (API key style) must never open a console endpoint.
        options.DefaultPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => !IsClientToken(ctx.User))
            .Build();

        // The reverse for the gateway: the token must belong to a client and carry the matching scope. A person's token is not enough.
        foreach (var scope in GatewayScopes.All)
            options.AddPolicy(GatewayScopes.PolicyName(scope), p => p
                .RequireAuthenticatedUser()
                .RequireAssertion(ctx => IsClientToken(ctx.User) && HasScope(ctx.User, scope)));

        options.AddPolicy(Playground, p => p
            .RequireAuthenticatedUser()
            .RequireAssertion(ctx => !IsClientToken(ctx.User))
            .RequireRole("Developer", "SecurityOfficer", "TenantAdmin"));
    }
}

public static class GatewayCallerExtensions
{
    /// <summary>
    /// Builds the caller from the verified token only. For an API client the "client" is the client id and the actor type is "client";
    /// for a person using the playground it is the user id and "user". Nothing here ever reads the request body.
    /// </summary>
    public static GatewayCaller ToGatewayCaller(this HttpContext http)
    {
        var user = http.User;
        var tenantId = Guid.Parse(user.FindFirst("tid")!.Value);
        var isClient = AuthPolicies.IsClientToken(user);
        var id = Guid.Parse((isClient ? user.FindFirst("client_id") : user.FindFirst("sub"))!.Value);
        return new GatewayCaller(
            tenantId, id,
            CanReidentify: isClient ? AuthPolicies.HasScope(user, GatewayScopes.Reidentify) : true,   // playground users are limited by policy, not scope
            Ip: http.Connection.RemoteIpAddress?.ToString(),
            ActorType: isClient ? "client" : "user");
    }
}
