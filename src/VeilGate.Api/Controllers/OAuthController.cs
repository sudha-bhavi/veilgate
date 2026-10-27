using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VeilGate.Application.ApiClients;

namespace VeilGate.Api.Controllers;

/// <summary>
/// OAuth 2.0 client credentials (RFC 6749 section 4.4, Backend doc 3.2). The client sends its id and secret either as HTTP Basic or as
/// form fields, never both, and gets a 15-minute bearer token for the gateway. Errors use the OAuth shape, { "error": "..." }, so
/// standard OAuth libraries understand them. Anonymous by nature, so it is rate limited per address like login.
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class OAuthController(IssueClientTokenHandler handler) : ControllerBase
{
    [HttpPost("~/oauth/token")]
    [HttpPost("~/api/v1/oauth/token")]
    [Consumes("application/x-www-form-urlencoded")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Token(
        [FromForm(Name = "grant_type")] string? grantType,
        [FromForm(Name = "client_id")] string? clientId,
        [FromForm(Name = "client_secret")] string? clientSecret,
        [FromForm(Name = "scope")] string? scope,
        CancellationToken ct)
    {
        if (grantType != "client_credentials") return OAuthError(400, "unsupported_grant_type");

        var usedBasic = TryReadBasic(Request.Headers.Authorization.ToString(), out var basicId, out var basicSecret);
        if (usedBasic)
        {
            // Two ways of proving who you are in one request is a mistake or an attack: refuse it rather than pick one.
            if (!string.IsNullOrEmpty(clientSecret) || (!string.IsNullOrEmpty(clientId) && clientId != basicId))
                return OAuthError(400, "invalid_request");
            clientId = basicId;
            clientSecret = basicSecret;
        }

        var result = await handler.HandleAsync(
            new IssueClientTokenCommand(clientId, clientSecret, scope, HttpContext.Connection.RemoteIpAddress?.ToString()), ct);

        if (result.IsFailure)
        {
            if (result.Error.Code == IssueClientTokenHandler.InvalidScope.Code) return OAuthError(400, "invalid_scope");
            if (result.Error.Code == IssueClientTokenHandler.InvalidClient.Code)
            {
                if (usedBasic) Response.Headers.WWWAuthenticate = "Basic realm=\"veilgate\"";
                return OAuthError(401, "invalid_client");
            }
            return OAuthError(500, "server_error");
        }

        // RFC 6749 section 5.1: a token response must never be cached.
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return Ok(new
        {
            access_token = result.Value.AccessToken,
            token_type = "Bearer",
            expires_in = result.Value.ExpiresInSeconds,
            scope = result.Value.Scope
        });
    }

    private JsonResult OAuthError(int status, string error) => new(new { error }) { StatusCode = status };

    /// <summary>RFC 6749 section 2.3.1: Basic carries "id:secret", each part form-urlencoded first.</summary>
    private static bool TryReadBasic(string header, out string id, out string secret)
    {
        id = secret = string.Empty;
        if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
            var colon = raw.IndexOf(':');
            if (colon < 0) return false;
            id = Uri.UnescapeDataString(raw[..colon].Replace('+', ' '));
            secret = Uri.UnescapeDataString(raw[(colon + 1)..].Replace('+', ' '));
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
