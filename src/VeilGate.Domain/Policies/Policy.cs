using VeilGate.Domain.Common;

namespace VeilGate.Domain.Policies;

/// <summary>
/// A named policy and the pointers to its versions: the published one (what the gateway uses) and at most one draft.
/// Versions are separate rows; the methods here take the versions they act on so the rules about several versions live in one place.
/// </summary>
public sealed class Policy : TenantEntity
{
    private Policy() { }

    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Guid? CurrentVersionId { get; private set; }
    public Guid? DraftVersionId { get; private set; }
    public int LastVersionNo { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }

    public bool IsArchived => ArchivedAt is not null;

    public static Result<Policy> Create(Guid tenantId, string name, string? description)
    {
        if (tenantId == Guid.Empty) return Error.Validation("validation.failed", "Tenant is required.");
        name = (name ?? string.Empty).Trim();
        if (name.Length is < 1 or > 80) return Error.Validation("validation.failed", "Policy name must be 1 to 80 characters.");
        description = description?.Trim();
        if (description is { Length: > 500 }) return Error.Validation("validation.failed", "Description is at most 500 characters.");

        var p = new Policy { Name = name, Description = description };
        p.SetTenant(tenantId);
        return p;
    }

    public Result<PolicyVersion> NewDraft(string definition, Guid authorId)
    {
        if (IsArchived) return Error.Conflict("resource.conflict", "An archived policy cannot be changed.");
        if (DraftVersionId is not null) return Error.Conflict("resource.conflict", "This policy already has a draft.");

        var draft = PolicyVersion.CreateDraft(TenantId, Id, LastVersionNo + 1, definition, authorId);
        if (draft.IsFailure) return draft;

        LastVersionNo = draft.Value.VersionNo;
        DraftVersionId = draft.Value.Id;
        return draft;
    }

    /// <summary>Publishes the draft. The version that was current (if any) becomes Archived, so exactly one version is live.</summary>
    public Result Publish(PolicyVersion draft, PolicyVersion? current, Guid approverId, DateTimeOffset now)
    {
        if (IsArchived) return Result.Failure(Error.Conflict("resource.conflict", "An archived policy cannot be changed."));
        if (!IsDraftOfThisPolicy(draft)) return Result.Failure(Error.NotFound());
        if (!IsCurrentOfThisPolicy(current)) return Result.Failure(Error.NotFound());

        var published = draft.Publish(approverId, now);
        if (published.IsFailure) return published;

        current?.Archive();
        CurrentVersionId = draft.Id;
        DraftVersionId = null;
        return Result.Success();
    }

    /// <summary>A rejected version stays in the history; the policy is free to start a new draft.</summary>
    public Result Reject(PolicyVersion draft, string reason)
    {
        if (!IsDraftOfThisPolicy(draft)) return Result.Failure(Error.NotFound());
        var rejected = draft.Reject(reason);
        if (rejected.IsFailure) return rejected;

        DraftVersionId = null;
        return Result.Success();
    }

    /// <summary>Publishes a new version with an older version's text. Any draft in progress is left alone.</summary>
    public Result<PolicyVersion> Rollback(PolicyVersion target, PolicyVersion? current, Guid actorId, DateTimeOffset now)
    {
        if (IsArchived) return Error.Conflict("resource.conflict", "An archived policy cannot be changed.");
        if (target.PolicyId != Id || target.TenantId != TenantId) return Error.NotFound();
        if (!IsCurrentOfThisPolicy(current)) return Error.NotFound();

        var rolledBack = PolicyVersion.CreateRollback(target, LastVersionNo + 1, actorId, now);
        if (rolledBack.IsFailure) return rolledBack;

        current?.Archive();
        LastVersionNo = rolledBack.Value.VersionNo;
        CurrentVersionId = rolledBack.Value.Id;
        return rolledBack;
    }

    public Result Archive(DateTimeOffset now)
    {
        if (IsArchived) return Result.Failure(Error.Conflict("resource.conflict", "Policy is already archived."));
        ArchivedAt = now;
        return Result.Success();
    }

    private bool IsDraftOfThisPolicy(PolicyVersion v) =>
        v.PolicyId == Id && v.TenantId == TenantId && DraftVersionId == v.Id;

    // The caller must hand over exactly the version that is current: none when nothing is published yet, otherwise that row.
    private bool IsCurrentOfThisPolicy(PolicyVersion? v) =>
        v is null ? CurrentVersionId is null : v.PolicyId == Id && v.TenantId == TenantId && CurrentVersionId == v.Id;
}