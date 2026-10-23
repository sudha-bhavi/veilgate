namespace VeilGate.Domain.Common;

public abstract class Entity
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();
    public DateTimeOffset CreatedAt { get; protected set; } = DateTimeOffset.UtcNow;
}

/// <summary>Every tenant-owned entity carries TenantId. It is set from the validated token, never from request data.</summary>
public abstract class TenantEntity : Entity
{
    public Guid TenantId { get; protected set; }
    protected TenantEntity() { }
    protected TenantEntity(Guid tenantId) => TenantId = tenantId;
    protected void SetTenant(Guid tenantId) => TenantId = tenantId;
}
