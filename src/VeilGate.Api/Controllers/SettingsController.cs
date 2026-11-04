using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VeilGate.Api.Options;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.Domain;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// The Settings page, read-only: the organisation's name and status, and the rules that apply to it. Every rule here is a server-wide
/// setting (in configuration or in code), not a per-organisation choice, so there is nothing to change from the console. Tenant Admins only.
/// The values are read from the same places the rules are enforced, so this page cannot drift from what actually happens.
/// </summary>
[ApiController]
[Route("api/v1/settings")]
[Authorize(Policy = AuthPolicies.UserAdmin)]
public sealed class SettingsController(IUserRepository users, IOptions<AuthOptions> auth) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        // Row-level security leaves only the caller's own organisation, so this can never show another one.
        var name = await users.GetTenantNameAsync(ct);
        if (name is null) return Fail(Error.NotFound());
        var active = await users.IsTenantActiveAsync(ct);

        return Ok(new
        {
            organisation = new { name, status = active ? "Active" : "Suspended" },
            rules = new
            {
                accessTokenMinutes = auth.Value.AccessTokenMinutes,
                sessionIdleHours = (int)Session.IdleTimeout.TotalHours,
                sessionMaxDays = (int)Session.AbsoluteTimeout.TotalDays,
                lockoutAttempts = VeilGate.Domain.User.MaxFailedAttempts,
                lockoutMinutes = (int)VeilGate.Domain.User.LockDuration.TotalMinutes,
                mfaRequired = true,
            }
        });
    }
}
