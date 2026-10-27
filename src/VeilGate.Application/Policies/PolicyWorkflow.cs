using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

// The approval workflow (Backend doc 4.3): validate -> submit -> publish or reject, rollback, archive.
// Every step that changes a policy writes an audit event; the policy text is never put in the log.

public sealed record PolicyValidation(bool Valid, IReadOnlyList<PolicyIssue> Errors, IReadOnlyList<string> Warnings);

/// <summary>Checks the draft (or, when there is none, the published version) and reports every problem with its line and column.</summary>
public sealed class ValidatePolicyHandler(IPolicyRepository policies)
{
    public async Task<Result<PolicyValidation>> HandleAsync(Guid policyId, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(policyId, ct);
        if (policy is null) return Error.NotFound();

        var (draft, current) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        var version = draft ?? current;
        if (version is null) return Error.Conflict("policy.no_draft", "This policy has no text to check.");

        var parsed = PolicyParser.Parse(version.Definition);
        return new PolicyValidation(parsed.IsValid, parsed.Errors, parsed.Warnings);
    }
}

public sealed record SubmitPolicyCommand(Guid PolicyId, string Summary, PolicyActor Actor);

public sealed class SubmitPolicyHandler(IPolicyRepository policies, PolicyAuditor auditor, IClock clock)
{
    public async Task<Result> HandleAsync(SubmitPolicyCommand cmd, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Result.Failure(Error.NotFound());
        if (policy.IsArchived) return Result.Failure(Error.Conflict("resource.conflict", "An archived policy cannot be changed."));

        var (draft, _) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        if (draft is null) return Result.Failure(Error.Conflict("policy.no_draft", "This policy has no draft to submit."));

        // Only text that validates may go to a reviewer.
        var parsed = PolicyParser.Parse(draft.Definition);
        if (!parsed.IsValid)
            return Result.Failure(Error.Unprocessable("policy.invalid_definition", "Fix the errors in the draft before submitting.")
                .With("errors", parsed.Errors));

        var submitted = draft.Submit(cmd.Actor.UserId, cmd.Summary, clock.UtcNow);
        if (submitted.IsFailure) return submitted;

        await policies.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "policy.submit", policy.Id, "success",
            new Dictionary<string, string> { ["version"] = draft.VersionNo.ToString() }, ct);
        return Result.Success();
    }
}

public sealed record PublishPolicyCommand(Guid PolicyId, PolicyActor Actor);

/// <summary>
/// Approves and publishes the submitted draft. The person publishing may not be the person who submitted it (Backend doc 2.2,
/// NotSelfApproval). A refused attempt is audited as "denied". The caller's recent-MFA proof is checked by the controller.
/// </summary>
public sealed class PublishPolicyHandler(IPolicyRepository policies, PolicyAuditor auditor, IClock clock)
{
    public async Task<Result<int>> HandleAsync(PublishPolicyCommand cmd, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Error.NotFound();

        var (draft, current) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        if (draft is null || draft.State != PolicyState.PendingApproval)
            return Error.Conflict("policy.not_pending", "Nothing is waiting for approval.");

        var parsed = PolicyParser.Parse(draft.Definition);
        if (!parsed.IsValid)
            return Error.Unprocessable("policy.invalid_definition", "The submitted policy does not validate.").With("errors", parsed.Errors);

        var published = policy.Publish(draft, current, cmd.Actor.UserId, clock.UtcNow);
        if (published.IsFailure)
        {
            await auditor.WriteAsync(cmd.Actor, "policy.publish", policy.Id, "denied",
                new Dictionary<string, string> { ["reason"] = published.Error.Code }, ct);
            return published.Error;
        }

        await policies.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "policy.publish", policy.Id, "success",
            new Dictionary<string, string> { ["version"] = draft.VersionNo.ToString(), ["approver"] = cmd.Actor.UserId.ToString() }, ct);
        return draft.VersionNo;
    }
}

public sealed record RejectPolicyCommand(Guid PolicyId, string Reason, PolicyActor Actor);

public sealed class RejectPolicyHandler(IPolicyRepository policies, PolicyAuditor auditor)
{
    public async Task<Result> HandleAsync(RejectPolicyCommand cmd, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Result.Failure(Error.NotFound());

        var (draft, _) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        if (draft is null || draft.State != PolicyState.PendingApproval)
            return Result.Failure(Error.Conflict("policy.not_pending", "Nothing is waiting for approval."));

        var rejected = policy.Reject(draft, cmd.Reason);
        if (rejected.IsFailure) return rejected;

        await policies.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "policy.reject", policy.Id, "success", new Dictionary<string, string>
        {
            ["version"] = draft.VersionNo.ToString(),
            ["reason"] = PolicyAuditor.Short(cmd.Reason.Trim())
        }, ct);
        return Result.Success();
    }
}

public sealed record RollbackPolicyCommand(Guid PolicyId, Guid VersionId, PolicyActor Actor);

/// <summary>Publishes an older version's text again, as a new version, so history is never rewritten.</summary>
public sealed class RollbackPolicyHandler(IPolicyRepository policies, PolicyAuditor auditor, IClock clock)
{
    public async Task<Result<int>> HandleAsync(RollbackPolicyCommand cmd, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Error.NotFound();

        var target = await policies.FindVersionAsync(cmd.VersionId, ct);
        if (target is null || target.PolicyId != policy.Id) return Error.NotFound();
        if (!target.IsIntact) return Error.Conflict("policy.tampered", "The stored text of that version no longer matches its hash.");

        var (_, current) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        var rolledBack = policy.Rollback(target, current, cmd.Actor.UserId, clock.UtcNow);
        if (rolledBack.IsFailure) return rolledBack.Error;

        policies.AddVersion(rolledBack.Value);
        await policies.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "policy.rollback", policy.Id, "success", new Dictionary<string, string>
        {
            ["version"] = rolledBack.Value.VersionNo.ToString(),
            ["target"] = target.VersionNo.ToString()
        }, ct);
        return rolledBack.Value.VersionNo;
    }
}

public sealed record ArchivePolicyCommand(Guid PolicyId, PolicyActor Actor);

public sealed class ArchivePolicyHandler(IPolicyRepository policies, PolicyAuditor auditor, IClock clock)
{
    public async Task<Result> HandleAsync(ArchivePolicyCommand cmd, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(cmd.PolicyId, ct);
        if (policy is null) return Result.Failure(Error.NotFound());

        var archived = policy.Archive(clock.UtcNow);
        if (archived.IsFailure) return archived;

        await policies.SaveChangesAsync(ct);
        await auditor.WriteAsync(cmd.Actor, "policy.archive", policy.Id, "success", null, ct);
        return Result.Success();
    }
}
