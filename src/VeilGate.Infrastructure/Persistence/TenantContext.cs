using VeilGate.Application.Abstractions;

namespace VeilGate.Infrastructure.Persistence;

public sealed class TenantContext : ITenantContext
{
    private Guid? _tenantId;
    public bool IsSet => _tenantId.HasValue;
    public Guid TenantId => _tenantId ?? throw new InvalidOperationException("Tenant context has not been set for this request.");

    public void Set(Guid tenantId)
    {
        if (_tenantId.HasValue) throw new InvalidOperationException("Tenant context can only be set once per request.");
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        _tenantId = tenantId;
    }
}
