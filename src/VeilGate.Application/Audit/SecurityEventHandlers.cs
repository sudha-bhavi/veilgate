using VeilGate.Application.Abstractions;
using VeilGate.Application.Policies;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;

namespace VeilGate.Application.Audit;

/// <param name="CallerId">The signed-in person.</param>
/// <param name="SeesWholeOrganization">True for Tenant Admins and Security Officers. Everyone else sees only events about themselves.</param>
/// <param name="UserId">Narrow to one person. Only the people who see the whole organization may ask for someone other than themselves.</param>
public sealed record ListSecurityEventsQuery(
    Guid CallerId, bool SeesWholeOrganization, Guid? UserId, string? Severity, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// GET /security-events (Backend doc 3.3: "filter depends on role"). The rule lives here, not in the controller:
/// a person without oversight is always narrowed to their own events, and asking for someone else's is refused.
/// </summary>
public sealed class ListSecurityEventsHandler(ISecurityEventReader reader)
{
    public const int MaxPageSize = 100;

    public static readonly Error OnlyYourOwn =
        Error.Forbidden("security_events.own_only", "You can only see security events about your own account.");

    public async Task<Result<Paged<SecurityEventView>>> HandleAsync(
        ListSecurityEventsQuery query, int page, int pageSize, CancellationToken ct = default)
    {
        Severity? severity = null;
        if (!string.IsNullOrEmpty(query.Severity))
        {
            if (!Enum.TryParse<Severity>(query.Severity, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                return Error.Validation("validation.failed", "Severity must be Info, Low, Medium, High or Critical.");
            severity = parsed;
        }
        if (query.From is { } from && query.To is { } to && from > to)
            return Error.Validation("validation.failed", "The start of the time range must be before its end.");

        var userId = query.UserId;
        if (!query.SeesWholeOrganization)
        {
            if (userId is { } asked && asked != query.CallerId) return OnlyYourOwn;
            userId = query.CallerId;
        }

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var (items, total) = await reader.ListAsync(new SecurityEventFilter(userId, severity, query.From, query.To), page, pageSize, ct);
        return new Paged<SecurityEventView>(items, page, pageSize, total);
    }
}

/// <summary>The people of the tenant, for the "User" filter. Only offered to those who see the whole organization (the policy checks).</summary>
public sealed class ListSecurityEventPeopleHandler(ISecurityEventReader reader)
{
    public Task<IReadOnlyList<SecurityEventPerson>> HandleAsync(CancellationToken ct = default) => reader.ListPeopleAsync(ct);
}
