using VeilGate.Domain.Audit;

namespace VeilGate.Application.Abstractions;

public interface ISecurityEventWriter
{
    /// <summary>Records a security event for the current tenant. Never pass content; the event type and counts are enough.</summary>
    Task WriteAsync(SecurityEvent securityEvent, CancellationToken ct = default);
}
