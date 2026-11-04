using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Requests;

/// <summary>A page of the current tenant's gateway requests, newest first, narrowed by the filter.</summary>
public sealed class ListGatewayRequestsHandler(IGatewayRequestReader reader)
{
    public const int MaxPageSize = 100;

    private static readonly HashSet<string> Statuses = ["success", "failure", "denied"];

    public async Task<Result<Paged<GatewayRequestView>>> HandleAsync(GatewayRequestFilter filter, int page, int pageSize, CancellationToken ct = default)
    {
        if (filter.Status is not null && !Statuses.Contains(filter.Status))
            return Error.Validation("validation.failed", "Status must be success, failure or denied.");
        if (filter.From is { } from && filter.To is { } to && from > to)
            return Error.Validation("validation.failed", "The start of the time range must be before its end.");
        if (filter.Action is { Length: > 30 } || filter.RequestId is { Length: > 40 })
            return Error.Validation("validation.failed", "A filter value is too long.");

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var (items, total) = await reader.ListAsync(filter, page, pageSize, ct);
        return new Paged<GatewayRequestView>(items, page, pageSize, total);
    }
}

public sealed class GetGatewayRequestHandler(IGatewayRequestReader reader)
{
    public async Task<Result<GatewayRequestView>> HandleAsync(Guid id, CancellationToken ct = default) =>
        await reader.FindAsync(id, ct) is { } view ? view : Error.NotFound("request.not_found", "Request not found.");
}

/// <summary>The action names in the current tenant's requests, for the console's filter list.</summary>
public sealed class ListRequestActionsHandler(IGatewayRequestReader reader)
{
    public Task<IReadOnlyList<string>> HandleAsync(CancellationToken ct = default) => reader.ListActionsAsync(ct);
}
