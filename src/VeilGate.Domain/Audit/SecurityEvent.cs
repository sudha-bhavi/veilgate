using System.Net;
using VeilGate.Domain.Common;

namespace VeilGate.Domain.Audit;

public enum Severity { Info, Low, Medium, High, Critical }

/// <summary>
/// Something a security officer should look at: a blocked leak, a reused refresh token, a brute-force attempt (Database doc 4.1).
/// Unlike the audit log this table is not a hash chain: events can be acknowledged. Like the audit log it never holds content.
/// </summary>
public sealed class SecurityEvent
{
    private SecurityEvent() { }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid TenantId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public Severity Severity { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public Guid? UserId { get; private set; }
    public Guid? ClientId { get; private set; }
    public string? Ip { get; private set; }
    public IReadOnlyDictionary<string, string> Details { get; private set; } = new Dictionary<string, string>();
    public Guid? AcknowledgedBy { get; private set; }
    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public static Result<SecurityEvent> Create(
        Guid tenantId, DateTimeOffset now, Severity severity, string type,
        Guid? userId = null, Guid? clientId = null, string? ip = null, IReadOnlyDictionary<string, string>? details = null)
    {
        if (type.Length is < 1 or > 60) return Error.Validation("validation.failed", "Type must be 1 to 60 characters.");

        details ??= new Dictionary<string, string>();
        foreach (var (key, value) in details)
        {
            if (!AuditEvent.AllowedDetailKeys.Contains(key)) return Error.Validation("validation.failed", $"'{key}' is not an allowed detail.");
            if (value.Length > 200) return Error.Validation("validation.failed", $"Detail '{key}' is too long.");
        }

        if (ip is not null)
        {
            if (!IPAddress.TryParse(ip, out var parsed)) return Error.Validation("validation.failed", "IP address is not valid.");
            ip = parsed.ToString();
        }

        var utc = now.UtcDateTime;   // PostgreSQL keeps microseconds; cut the extra digit so what we hold equals what is stored
        return new SecurityEvent
        {
            TenantId = tenantId, OccurredAt = new DateTimeOffset(utc.Ticks - utc.Ticks % 10, TimeSpan.Zero),
            Severity = severity, Type = type, UserId = userId, ClientId = clientId, Ip = ip,
            Details = new Dictionary<string, string>(details),
        };
    }

    public Result Acknowledge(Guid userId, DateTimeOffset now)
    {
        if (AcknowledgedAt is not null) return Result.Failure(Error.Conflict("resource.conflict", "Already acknowledged."));
        AcknowledgedBy = userId;
        AcknowledgedAt = now;
        return Result.Success();
    }
}
