using VeilGate.Domain.Audit;

namespace VeilGate.Application.Abstractions;

public interface IAuditWriter
{
    /// <summary>Seals the event into the tenant's chain and saves it. Never records content, only the event.</summary>
    Task WriteAsync(AuditEvent auditEvent, CancellationToken ct = default);
}