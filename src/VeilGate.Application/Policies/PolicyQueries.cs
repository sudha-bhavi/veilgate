using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Policies;

namespace VeilGate.Application.Policies;

public sealed record PolicyVersionView(Guid Id, int VersionNo, string State, string Yaml, string? Summary, DateTimeOffset? PublishedAt);

public sealed record PolicyDetail(
    Guid Id, string Name, string? Description, string Status,
    PolicyVersionView? Current, PolicyVersionView? Draft, string Etag, DateTimeOffset CreatedAt);

public sealed class GetPolicyHandler(IPolicyRepository policies)
{
    public async Task<Result<PolicyDetail>> HandleAsync(Guid policyId, CancellationToken ct = default)
    {
        var policy = await policies.FindAsync(policyId, ct);
        if (policy is null) return Error.NotFound();

        var (draft, current) = await PolicyLoad.VersionsAsync(policies, policy, ct);
        return new PolicyDetail(
            policy.Id, policy.Name, policy.Description,
            PolicyStatus.Of(policy.IsArchived, current is not null, draft?.State),
            View(current), View(draft), PolicyEtag.Of(draft ?? current), policy.CreatedAt);
    }

    private static PolicyVersionView? View(PolicyVersion? v) =>
        v is null ? null : new PolicyVersionView(v.Id, v.VersionNo, v.State.ToString(), v.Definition, v.ChangeNote, v.PublishedAt);
}

public sealed class ListPoliciesHandler(IPolicyRepository policies)
{
    public async Task<Result<Paged<PolicyListItem>>> HandleAsync(string? status, string? search, int page, int pageSize, CancellationToken ct = default)
    {
        if (status is not null && !PolicyStatus.All.Contains(status))
            return Error.Validation("validation.failed", "Unknown status.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var result = await policies.ListAsync(status, search, page, pageSize, ct);
        return new Paged<PolicyListItem>(result.Items, page, pageSize, result.Total);
    }
}

public sealed record PolicyVersionListItem(
    Guid Id, int VersionNo, string State, Guid CreatedBy, Guid? SubmittedBy, Guid? ApprovedBy,
    DateTimeOffset? PublishedAt, string? Summary);

public sealed class ListPolicyVersionsHandler(IPolicyRepository policies)
{
    public async Task<Result<Paged<PolicyVersionListItem>>> HandleAsync(Guid policyId, int page, int pageSize, CancellationToken ct = default)
    {
        if (await policies.FindAsync(policyId, ct) is null) return Error.NotFound();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var (items, total) = await policies.ListVersionsAsync(policyId, page, pageSize, ct);
        var rows = items.Select(v => new PolicyVersionListItem(
            v.Id, v.VersionNo, v.State.ToString(), v.CreatedBy, v.SubmittedBy, v.ApprovedBy, v.PublishedAt, v.ChangeNote)).ToList();
        return new Paged<PolicyVersionListItem>(rows, page, pageSize, total);
    }
}

public sealed record PolicyDiff(int From, int To, string UnifiedDiff);

public sealed class DiffPolicyHandler(IPolicyRepository policies)
{
    public async Task<Result<PolicyDiff>> HandleAsync(Guid policyId, int from, int to, CancellationToken ct = default)
    {
        if (await policies.FindAsync(policyId, ct) is null) return Error.NotFound();

        var a = await policies.FindVersionByNumberAsync(policyId, from, ct);
        var b = await policies.FindVersionByNumberAsync(policyId, to, ct);
        if (a is null || b is null) return Error.NotFound();

        var diff = UnifiedDiff.Create($"version {a.VersionNo}", $"version {b.VersionNo}", a.Definition, b.Definition);
        if (diff is null) return Error.Unprocessable("policy.diff_too_large", "These versions are too long to compare.");
        return new PolicyDiff(a.VersionNo, b.VersionNo, diff);
    }
}
