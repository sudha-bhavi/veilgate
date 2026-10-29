using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Audit;

/// <summary>A page of the current tenant's audit log, newest first, narrowed by the filter.</summary>
public sealed class ListAuditEventsHandler(IAuditEventReader reader)
{
    public const int MaxPageSize = 100;

    // The same words AuditEvent.Create accepts: anything else could never match, so it is a mistake worth reporting.
    private static readonly HashSet<string> ActorTypes = ["user", "client", "system"];
    private static readonly HashSet<string> Results = ["success", "failure", "denied"];

    public async Task<Result<Paged<AuditEventView>>> HandleAsync(AuditEventFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        if (filter.ActorType is not null && !ActorTypes.Contains(filter.ActorType))
            return Error.Validation("validation.failed", "Actor type must be user, client or system.");
        if (filter.Result is not null && !Results.Contains(filter.Result))
            return Error.Validation("validation.failed", "Result must be success, failure or denied.");
        if (filter.From is { } from && filter.To is { } to && from > to)
            return Error.Validation("validation.failed", "The start of the time range must be before its end.");
        if (filter.Action is { Length: > 60 } || filter.ResourceType is { Length: > 40 })
            return Error.Validation("validation.failed", "A filter value is too long.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var (items, total) = await reader.ListAsync(filter, page, pageSize, ct);
        return new Paged<AuditEventView>(items, page, pageSize, total);
    }
}

/// <summary>The action names in the current tenant's log, for the console's filter list.</summary>
public sealed class ListAuditActionsHandler(IAuditEventReader reader)
{
    public Task<IReadOnlyList<string>> HandleAsync(CancellationToken ct = default) => reader.ListActionsAsync(ct);
}

/// <summary>
/// Recomputes the current tenant's whole hash chain (Backend doc 6.4). Ok means no event was deleted, changed or reordered;
/// if one was, the answer names the first broken sequence number.
/// </summary>
public sealed class VerifyAuditChainHandler(IAuditChainVerifier verifier, ITenantContext tenant)
{
    public Task<ChainVerification> HandleAsync(CancellationToken ct = default) => verifier.VerifyAsync(tenant.TenantId, ct: ct);
}
