using VeilGate.Domain.Common;
using System.Net;

namespace VeilGate.Domain.Audit;

/// <summary>One line of the tamper-evident audit log (Database doc 4.1). It records who did what, never the content they handled.</summary>
public sealed class AuditEvent
{
    public static readonly string Genesis = new('0', 64);   // the "previous hash" of the very first event of a tenant

    private static readonly HashSet<string> ActorTypes = ["user", "client", "system"];
    private static readonly HashSet<string> Results = ["success", "failure", "denied"];

    /// <summary>The only keys allowed in Details. A fixed list stops raw text or personal data slipping into the log by accident.</summary>
    public static readonly IReadOnlySet<string> AllowedDetailKeys = new HashSet<string>
    {
        "version", "approver", "reason", "count", "status", "clientId", "policyVersion", "entities",
        "method", "role", "scope", "target", "latencyMs", "provider", "model", "requestId", "types",
    };

    private AuditEvent() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public long Seq { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public string ActorType { get; private set; } = "system";
    public Guid? ActorId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string? ResourceType { get; private set; }
    public Guid? ResourceId { get; private set; }
    public string Result { get; private set; } = "success";
    public string? Ip { get; private set; }
    public IReadOnlyDictionary<string, string> Details { get; private set; } = new Dictionary<string, string>();
    public string PrevHash { get; private set; } = Genesis;
    public string Hash { get; private set; } = string.Empty;

    public static Result<AuditEvent> Create(
        Guid tenantId, DateTimeOffset now, string actorType, Guid? actorId, string action,
        string? resourceType = null, Guid? resourceId = null, string result = "success", string? ip = null,
        IReadOnlyDictionary<string, string>? details = null)
    {
        if (!ActorTypes.Contains(actorType)) return Error.Validation("validation.failed", "Actor type must be user, client or system.");
        if (!Results.Contains(result)) return Error.Validation("validation.failed", "Result must be success, failure or denied.");
        if (action.Length is < 1 or > 60) return Error.Validation("validation.failed", "Action must be 1 to 60 characters.");

        details ??= new Dictionary<string, string>();
        foreach (var (key, value) in details)
        {
            if (!AllowedDetailKeys.Contains(key)) return Error.Validation("validation.failed", $"'{key}' is not an allowed audit detail.");
            if (value.Length > 200) return Error.Validation("validation.failed", $"Audit detail '{key}' is too long.");
        }

        if (ip is not null)
        {
            if (!IPAddress.TryParse(ip, out var parsed)) return Error.Validation("validation.failed", "IP address is not valid.");
            ip = parsed.ToString();   // one canonical spelling, so the hash survives a round trip through PostgreSQL's inet type
        }
        // PostgreSQL stores time to the microsecond, .NET to 100 nanoseconds. If we kept the extra digit, the hash we compute now
        // would differ from the hash recomputed from the database later, and verification would wrongly report tampering.
        var utc = now.UtcDateTime;
        var micro = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero);

        return new AuditEvent
        {
            TenantId = tenantId, OccurredAt = micro, ActorType = actorType, ActorId = actorId, Action = action,
            ResourceType = resourceType, ResourceId = resourceId, Result = result, Ip = ip,
            Details = new Dictionary<string, string>(details),
        };
    }

    /// <summary>Called by the writer, under a lock, to place this event in the chain.</summary>
    public void Seal(long seq, string prevHash)
    {
        Seq = seq;
        PrevHash = prevHash;
        Hash = AuditHasher.Compute(this);
    }
}