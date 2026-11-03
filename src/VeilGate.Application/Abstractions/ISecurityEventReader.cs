using VeilGate.Domain.Audit;

namespace VeilGate.Application.Abstractions;

/// <summary>What the security events can be narrowed to. Every part is optional; all given parts must match.</summary>
public sealed record SecurityEventFilter(Guid? UserId, Severity? Severity, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// One security event as the console shows it. The names are looked up when the page is read (the table stores only ids);
/// they are null when the person or client no longer exists, or when the event has none.
/// </summary>
public sealed record SecurityEventView(
    Guid Id, DateTimeOffset OccurredAt, string Severity, string Type, Guid? UserId, string? UserName,
    Guid? ClientId, string? ClientName, string? Ip, IReadOnlyDictionary<string, string> Details);

/// <summary>A person of the tenant, for the "User" filter on the Security events tab.</summary>
public sealed record SecurityEventPerson(Guid Id, string Name, string Email);

/// <summary>Read-only access to the current tenant's security events, newest first.</summary>
public interface ISecurityEventReader
{
    Task<(IReadOnlyList<SecurityEventView> Items, int Total)> ListAsync(
        SecurityEventFilter filter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Everyone in the current tenant, by name.</summary>
    Task<IReadOnlyList<SecurityEventPerson>> ListPeopleAsync(CancellationToken ct = default);
}
