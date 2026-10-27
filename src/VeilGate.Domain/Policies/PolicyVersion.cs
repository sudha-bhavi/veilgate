using System.Security.Cryptography;
using System.Text;
using VeilGate.Domain.Common;

namespace VeilGate.Domain.Policies;

/// <summary>
/// One version of a policy's text and where it is in the approval workflow:
/// Draft -> PendingApproval -> Published (-> Archived when a newer version is published), or Rejected.
/// </summary>
public sealed class PolicyVersion : TenantEntity
{
    public static readonly Error SelfApproval =
        Error.Forbidden("policy.self_approval", "A policy cannot be published by the person who submitted it.");

    private PolicyVersion() { }

    public Guid PolicyId { get; private set; }
    public int VersionNo { get; private set; }
    public PolicyState State { get; private set; }
    public string Definition { get; private set; } = string.Empty;
    public string DefinitionSha256 { get; private set; } = string.Empty;
    public Guid CreatedBy { get; private set; }
    public Guid? SubmittedBy { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public string? ChangeNote { get; private set; }
    public string? RejectReason { get; private set; }
        /// <summary>False if the text no longer matches the hash taken when it was saved (edited behind the application's back).</summary>
    public bool IsIntact => Hash(Definition).Equals(DefinitionSha256, StringComparison.Ordinal);

    public static Result<PolicyVersion> CreateDraft(Guid tenantId, Guid policyId, int versionNo, string definition, Guid createdBy)
    {
        if (tenantId == Guid.Empty || policyId == Guid.Empty || createdBy == Guid.Empty)
            return Error.Validation("validation.failed", "Tenant, policy and author are required.");
        if (versionNo < 1)
            return Error.Validation("validation.failed", "Version number must be 1 or more.");
        if (string.IsNullOrWhiteSpace(definition))
            return Error.Validation("validation.failed", "Policy text is required.");

        var v = new PolicyVersion
        {
            PolicyId = policyId,
            VersionNo = versionNo,
            State = PolicyState.Draft,
            Definition = definition,
            DefinitionSha256 = Hash(definition),
            CreatedBy = createdBy
        };
        v.SetTenant(tenantId);
        return v;
    }

       /// <summary>
    /// A rollback publishes a copy of an older version's text as a brand-new version. The text was already approved once,
    /// so the person rolling back is the approver and nobody "submitted" it (SubmittedBy stays empty).
    /// </summary>
    public static Result<PolicyVersion> CreateRollback(PolicyVersion source, int versionNo, Guid actorId, DateTimeOffset now)
    {
        if (source.State != PolicyState.Archived)
            return Error.Conflict("resource.conflict", "Only an earlier, archived version can be rolled back to.");

        var v = new PolicyVersion
        {
            PolicyId = source.PolicyId,
            VersionNo = versionNo,
            State = PolicyState.Published,
            Definition = source.Definition,
            DefinitionSha256 = source.DefinitionSha256,
            CreatedBy = actorId,
            ApprovedBy = actorId,
            ApprovedAt = now,
            PublishedAt = now,
            ChangeNote = $"Rollback to version {source.VersionNo}"
        };
        v.SetTenant(source.TenantId);
        return v;
    }
   
    /// <summary>Only a draft can be edited. Once submitted, the text is frozen so what was reviewed is what gets published.</summary>
    public Result EditDraft(string definition)
    {
        if (State != PolicyState.Draft) return Result.Failure(NotInState("edited", PolicyState.Draft));
        if (string.IsNullOrWhiteSpace(definition))
            return Result.Failure(Error.Validation("validation.failed", "Policy text is required."));
        Definition = definition;
        DefinitionSha256 = Hash(definition);
        return Result.Success();
    }

    public Result Submit(Guid submitterId, string summary, DateTimeOffset now)
    {
        if (State != PolicyState.Draft) return Result.Failure(NotInState("submitted", PolicyState.Draft));
        summary = (summary ?? string.Empty).Trim();
        if (summary.Length is < 10 or > 500)
            return Result.Failure(Error.Validation("validation.failed", "Summary must be 10 to 500 characters."));

        State = PolicyState.PendingApproval;
        SubmittedBy = submitterId;
        SubmittedAt = now;
        ChangeNote = summary;
        return Result.Success();
    }

    public Result Publish(Guid approverId, DateTimeOffset now)
    {
        if (State != PolicyState.PendingApproval) return Result.Failure(NotInState("published", PolicyState.PendingApproval));
        if (approverId == SubmittedBy) return Result.Failure(SelfApproval);
              if (!IsIntact)
            return Result.Failure(Error.Conflict("policy.tampered", "The policy text changed after it was submitted."));
        State = PolicyState.Published;
        ApprovedBy = approverId;
        ApprovedAt = now;
        PublishedAt = now;
        return Result.Success();
    }

    public Result Reject(string reason)
    {
        if (State != PolicyState.PendingApproval) return Result.Failure(NotInState("rejected", PolicyState.PendingApproval));
        reason = (reason ?? string.Empty).Trim();
        if (reason.Length is < 1 or > 500)
            return Result.Failure(Error.Validation("validation.failed", "A reason of up to 500 characters is required."));

        State = PolicyState.Rejected;
        RejectReason = reason;
        return Result.Success();
    }

    /// <summary>Called when a newer version is published (or the policy is archived). History stays readable.</summary>
    public Result Archive()
    {
        if (State != PolicyState.Published) return Result.Failure(NotInState("archived", PolicyState.Published));
        State = PolicyState.Archived;
        return Result.Success();
    }

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static Error NotInState(string verb, PolicyState required) =>
        Error.Conflict("resource.conflict", $"Only a {required} version can be {verb}.");

    
}