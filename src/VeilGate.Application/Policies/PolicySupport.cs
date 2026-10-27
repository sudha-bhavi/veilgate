using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

/// <summary>Who is doing this and from where. Both come from the verified token and the connection, never from the request body.</summary>
public sealed record PolicyActor(Guid UserId, string? Ip);

public static class PolicyEtag
{
    /// <summary>
    /// The ETag of what a person would be editing: the draft if there is one, otherwise the published version.
    /// It is the quoted hash of the text, so any change to the text changes it.
    /// </summary>
    public static string Of(PolicyVersion? editable) => $"\"{editable?.DefinitionSha256 ?? "none"}\"";
}

public static class PolicyStatus
{
    public const string Draft = "Draft";
    public const string PendingApproval = "PendingApproval";
    public const string Published = "Published";
    public const string Archived = "Archived";

    public static readonly IReadOnlySet<string> All = new HashSet<string> { Draft, PendingApproval, Published, Archived };

    /// <summary>What a policy is, as one word: archived, else waiting for approval, else live, else still a draft.</summary>
    public static string Of(bool archived, bool hasPublishedVersion, PolicyState? draftState) =>
        archived ? Archived
        : draftState == PolicyState.PendingApproval ? PendingApproval
        : hasPublishedVersion ? Published
        : Draft;
}

public sealed record PolicyListItem(Guid Id, string Name, string Status, int? CurrentVersion, bool HasDraft, DateTimeOffset CreatedAt);

/// <summary>What the repository hands back for a list: the rows of one page and how many policies match in all.</summary>
public sealed record PolicyPage(IReadOnlyList<PolicyListItem> Items, int Total);

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

internal static class PolicyLoad
{
    /// <summary>The draft and the published version of a policy, either of which may not exist.</summary>
    public static async Task<(PolicyVersion? Draft, PolicyVersion? Current)> VersionsAsync(IPolicyRepository policies, Policy policy, CancellationToken ct)
    {
        var draft = policy.DraftVersionId is { } d ? await policies.FindVersionAsync(d, ct) : null;
        var current = policy.CurrentVersionId is { } c ? await policies.FindVersionAsync(c, ct) : null;
        return (draft, current);
    }
}

/// <summary>Writes policy events to the audit log: who did what to which policy, never the policy text.</summary>
public sealed class PolicyAuditor(IAuditWriter audit, ITenantContext tenant, IClock clock, ILogger<PolicyAuditor> logger)
{
    public async Task WriteAsync(PolicyActor actor, string action, Guid policyId, string result,
        IReadOnlyDictionary<string, string>? details, CancellationToken ct)
    {
        var created = AuditEvent.Create(tenant.TenantId, clock.UtcNow, "user", actor.UserId, action,
            "policy", policyId, result, actor.Ip, details);
        if (created.IsFailure)
        {
            logger.LogError("Could not build audit event {Action}: {Code}", action, created.Error.Code);
            return;
        }
        await audit.WriteAsync(created.Value, ct);
    }

    /// <summary>Audit details are limited to 200 characters.</summary>
    public static string Short(string value) => value.Length > 200 ? value[..200] : value;
}
