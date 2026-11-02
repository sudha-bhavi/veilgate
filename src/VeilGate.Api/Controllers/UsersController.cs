using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Security;
using VeilGate.Api.Users;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Users;

namespace VeilGate.Api.Controllers;

/// <summary>
/// Managing the people of a tenant (Backend doc 5.1). Tenant Admins only. Changing a role and resetting someone's two-step sign-in
/// need a recent second-factor check. A person of another tenant is simply "not found". The rules (not yourself, not the last
/// admin) live in the handlers; this controller only maps requests and answers.
/// </summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = AuthPolicies.UserAdmin)]
public sealed class UsersController(IClock clock) : ApiControllerBase
{
    private UserActor Actor => new(Guid.Parse(User.FindFirst("sub")!.Value), CallerIp);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] ListUsersHandler handler, CancellationToken ct,
        [FromQuery] string? role = null, [FromQuery] string? status = null, [FromQuery] string? q = null,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var result = await handler.HandleAsync(role, status, q, page, pageSize, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    /// <summary>Invites someone. The person exists at once with status Invited. "emailSent" is false when the email failed: use resend.</summary>
    [HttpPost]
    [Consumes("application/json")]
    public async Task<IActionResult> Invite([FromBody] InviteUserRequest request, [FromServices] InviteUserHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new InviteUserCommand(request.Email, request.Role, Actor), ct);
        if (result.IsFailure) return Fail(result.Error);

        var r = result.Value;
        return Created($"/api/v1/users/{r.User.Id}", new
        {
            id = r.User.Id,
            email = r.User.Email,
            role = r.User.Role,
            status = r.User.Status,
            inviteExpiresAt = r.ExpiresAt,
            emailSent = r.EmailSent
        });
    }

    [HttpPatch("{id:guid}")]
    [Consumes("application/json")]
    public async Task<IActionResult> ChangeRole(Guid id, [FromBody] ChangeUserRoleRequest request, [FromServices] ChangeUserRoleHandler handler, CancellationToken ct)
    {
        if (RequireRecentMfa(clock.UtcNow) is { } stepUp) return stepUp;

        var result = await handler.HandleAsync(new ChangeUserRoleCommand(id, request.Role, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpPost("{id:guid}/disable")]
    public async Task<IActionResult> Disable(Guid id, [FromServices] DisableUserHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UserActionCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }

    [HttpPost("{id:guid}/enable")]
    public async Task<IActionResult> Enable(Guid id, [FromServices] EnableUserHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new UserActionCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }

    /// <summary>Sends a fresh link. Unlike the design doc's bare 204 this says whether the email went out, so the console can warn.</summary>
    [HttpPost("{id:guid}/invite/resend")]
    public async Task<IActionResult> ResendInvite(Guid id, [FromServices] ResendInvitationHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ResendInvitationCommand(id, Actor), ct);
        if (result.IsFailure) return Fail(result.Error);
        return Ok(new { emailSent = result.Value.EmailSent, inviteExpiresAt = result.Value.ExpiresAt });
    }

    [HttpPost("{id:guid}/mfa/reset")]
    public async Task<IActionResult> ResetMfa(Guid id, [FromServices] ResetMfaHandler handler, CancellationToken ct)
    {
        if (RequireRecentMfa(clock.UtcNow) is { } stepUp) return stepUp;

        var result = await handler.HandleAsync(new UserActionCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }
}
