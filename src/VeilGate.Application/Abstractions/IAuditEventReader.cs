namespace VeilGate.Application.Abstractions;

/// <summary>What the audit log can be narrowed to. Every part is optional; all given parts must match.</summary>
public sealed record AuditEventFilter(
    DateTimeOffset? From, DateTimeOffset? To, string? Action, string? ActorType, Guid? ActorId,
    string? ResourceType, Guid? ResourceId, string? Result);

/// <summary>
/// One audit event as the console shows it. ActorName is the person's or client's name at the time of reading (the log
/// itself stores only the id), or null when the actor no longer exists or is the system.
/// </summary>
public sealed record AuditEventView(
    Guid Id, long Seq, DateTimeOffset OccurredAt, string ActorType, Guid? ActorId, string? ActorName, string Action,
    string? ResourceType, Guid? ResourceId, string Result, string? Ip, IReadOnlyDictionary<string, string> Details, string Hash);

/// <summary>Read-only access to the current tenant's audit log, newest first. Nothing here can change an event.</summary>
public interface IAuditEventReader
{
    Task<(IReadOnlyList<AuditEventView> Items, int Total)> ListAsync(AuditEventFilter filter, int page, int pageSize, CancellationToken ct = default);

    /// <summary>The action names that occur in the tenant's log, for a filter list.</summary>
    Task<IReadOnlyList<string>> ListActionsAsync(CancellationToken ct = default);
}
