namespace VeilGate.Api.Middleware;

/// <summary>
/// Limits the console's fallback (which answers index.html so links like /policies/123 work) to paths outside the API.
/// Without it, a mistyped API URL would get the console's page with 200 instead of a plain 404.
/// </summary>
public sealed class ConsolePathConstraint : IRouteConstraint
{
    public const string Name = "console";

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        httpContext is not null && !SecurityHeadersMiddleware.IsApiPath(httpContext.Request.Path);
}
