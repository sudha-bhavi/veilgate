using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using VeilGate.Api.Policies;
using VeilGate.Api.Security;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Common;

namespace VeilGate.Api.Controllers;

/// <summary>
/// Policy workflow (Backend doc 4.3): draft, validate, submit, publish or reject, rollback, archive. Reading is open to every
/// signed-in role; writing needs Tenant Admin or Security Officer; publish, reject and rollback are the Security Officer's.
/// The tenant, the author and the approver all come from the token. A policy of another tenant is simply "not found".
/// </summary>
[ApiController]
[Route("api/v1/policies")]
public sealed class PoliciesController(IClock clock) : ApiControllerBase
{
    private PolicyActor Actor => new(Guid.Parse(User.FindFirst("sub")!.Value), HttpContext.Connection.RemoteIpAddress?.ToString());

    // ------------------------------------------------------------------ read

    [HttpGet]
    public async Task<IActionResult> List(
        [FromServices] ListPoliciesHandler handler, CancellationToken ct,
        [FromQuery] string? status = null, [FromQuery] string? q = null, [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var result = await handler.HandleAsync(status, q, page, pageSize, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromServices] GetPolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        if (result.IsFailure) return Fail(result.Error);
        Response.Headers.ETag = result.Value.Etag;
        return Ok(result.Value);
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<IActionResult> Versions(
        Guid id, [FromServices] ListPolicyVersionsHandler handler, CancellationToken ct,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25)
    {
        var result = await handler.HandleAsync(id, page, pageSize, ct);
        return result.IsFailure ? Fail(result.Error) : Ok(result.Value);
    }

    [HttpGet("{id:guid}/diff")]
    public async Task<IActionResult> Diff(
        Guid id, [FromQuery] int from, [FromQuery] int to, [FromServices] DiffPolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, from, to, ct);
        if (result.IsFailure) return Fail(result.Error);
        return Ok(new { from = result.Value.From, to = result.Value.To, unifiedDiff = result.Value.UnifiedDiff });
    }

    // ----------------------------------------------------------------- write

    [HttpPost]
    [Authorize(Policy = AuthPolicies.PolicyAuthor)]
    [Consumes("application/json")]
    public async Task<IActionResult> Create([FromBody] CreatePolicyRequest request, [FromServices] CreatePolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreatePolicyCommand(request.Name, request.Template, request.Description, Actor), ct);
        if (result.IsFailure) return Fail(result.Error);
        Response.Headers.ETag = result.Value.Etag;
        return Created($"/api/v1/policies/{result.Value.Id}", new { id = result.Value.Id, etag = result.Value.Etag });
    }

    [HttpPut("{id:guid}/draft")]
    [Authorize(Policy = AuthPolicies.PolicyAuthor)]
    [Consumes("application/json")]
    public async Task<IActionResult> SaveDraft(Guid id, [FromBody] SaveDraftRequest request, [FromServices] SaveDraftHandler handler, CancellationToken ct)
    {
        var ifMatch = Request.Headers.IfMatch.ToString();
        var result = await handler.HandleAsync(new SaveDraftCommand(id, request.Yaml, ifMatch.Length == 0 ? null : ifMatch, Actor.UserId), ct);
        if (result.IsFailure) return Fail(result.Error);
        Response.Headers.ETag = result.Value.Etag;
        return Ok(new { etag = result.Value.Etag, versionNo = result.Value.VersionNo });
    }

    [HttpPost("{id:guid}/validate")]
    [Authorize(Policy = AuthPolicies.PolicyAuthor)]
    public async Task<IActionResult> Validate(Guid id, [FromServices] ValidatePolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(id, ct);
        if (result.IsFailure) return Fail(result.Error);
        return Ok(new
        {
            valid = result.Value.Valid,
            errors = result.Value.Errors.Select(e => new { line = e.Line, column = e.Column, message = e.Message }),
            warnings = result.Value.Warnings
        });
    }

    [HttpPost("{id:guid}/submit")]
    [Authorize(Policy = AuthPolicies.PolicyAuthor)]
    [Consumes("application/json")]
    public async Task<IActionResult> Submit(Guid id, [FromBody] SubmitPolicyRequest request, [FromServices] SubmitPolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new SubmitPolicyCommand(id, request.Summary, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Accepted(new { status = PolicyStatus.PendingApproval });
    }

    [HttpPost("{id:guid}/publish")]
    [Authorize(Policy = AuthPolicies.PolicyReviewer)]
    public async Task<IActionResult> Publish(Guid id, [FromServices] PublishPolicyHandler handler, CancellationToken ct)
    {
        if (RequireRecentMfa() is { } stepUp) return stepUp;
        var result = await handler.HandleAsync(new PublishPolicyCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Ok(new { versionNumber = result.Value });
    }

    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = AuthPolicies.PolicyReviewer)]
    [Consumes("application/json")]
    public async Task<IActionResult> Reject(Guid id, [FromBody] RejectPolicyRequest request, [FromServices] RejectPolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RejectPolicyCommand(id, request.Reason, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : Ok(new { status = "Rejected" });
    }

    [HttpPost("{id:guid}/rollback")]
    [Authorize(Policy = AuthPolicies.PolicyReviewer)]
    [Consumes("application/json")]
    public async Task<IActionResult> Rollback(Guid id, [FromBody] RollbackPolicyRequest request, [FromServices] RollbackPolicyHandler handler, CancellationToken ct)
    {
        if (RequireRecentMfa() is { } stepUp) return stepUp;
        var result = await handler.HandleAsync(new RollbackPolicyCommand(id, request.VersionId, Actor), ct);
        if (result.IsFailure) return Fail(result.Error);
        return Created($"/api/v1/policies/{id}", new { versionNumber = result.Value });
    }

    [HttpPost("{id:guid}/archive")]
    [Authorize(Policy = AuthPolicies.PolicyAuthor)]
    public async Task<IActionResult> Archive(Guid id, [FromServices] ArchivePolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new ArchivePolicyCommand(id, Actor), ct);
        return result.IsFailure ? Fail(result.Error) : NoContent();
    }

    [HttpPost("{id:guid}/test")]
    [Authorize(Policy = AuthPolicies.PolicyTester)]
    [Consumes("application/json")]
    public async Task<IActionResult> Test(Guid id, [FromBody] TestPolicyRequest request, [FromServices] TestPolicyHandler handler, CancellationToken ct)
    {
        var result = await handler.HandleAsync(new TestPolicyCommand(id, request.Text, Actor.UserId), ct);
        if (result.IsFailure) return Fail(result.Error);
        return Ok(new
        {
            entities = result.Value.Entities,
            maskedText = result.Value.MaskedText,
            blocked = result.Value.Blocked,
            blockedTypes = result.Value.BlockedTypes
        });
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// Publishing and rolling back change what every request is checked against, so the signed-in person must have confirmed a
    /// second factor in the last five minutes. Otherwise: 401 with a hint that a fresh check is needed (Backend doc 7.2).
    /// </summary>
    private IActionResult? RequireRecentMfa()
    {
        if (AuthPolicies.HasRecentMfa(User, clock.UtcNow)) return null;
        Response.Headers.WWWAuthenticate = $"Bearer error=\"insufficient_user_authentication\", max_age={AuthPolicies.RecentMfaSeconds}";
        return Fail(Error.Unauthorized("auth.insufficient_user_authentication", "Confirm your identity again to continue."));
    }
}
