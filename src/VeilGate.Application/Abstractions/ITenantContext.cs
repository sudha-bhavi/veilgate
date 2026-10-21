namespace VeilGate.Application.Abstractions;

/// <summary>
/// The tenant of the current request. Set exactly once, from the validated JWT "tid" claim (Security doc gate T1/T2).
/// Reading it before it is set throws, so code can never run "tenant-less" by accident.
/// </summary>
public interface ITenantContext
{
    bool IsSet { get; }
    Guid TenantId { get; }
    void Set(Guid tenantId);
}
