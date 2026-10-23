using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;

namespace VeilGate.Api.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class JwksController(ISigningKeyProvider keys) : ControllerBase
{
    /// <summary>Public keys that let other systems verify VeilGate tokens (RFC 7517). Never contains private key material.</summary>
    [HttpGet(".well-known/jwks.json")]
    public IActionResult Get() => Ok(new { keys = keys.PublicJwks });
}
