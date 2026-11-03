using System.Diagnostics;
using System.Text.Json;
using VeilGate.Application.Abstractions;
using VeilGate.Domain.Common;
using VeilGate.Domain.Messaging;

namespace VeilGate.Infrastructure.Persistence;

public sealed class EfOutbox(VeilGateDbContext db, IClock clock) : IOutbox
{
    public Result Enqueue(Guid tenantId, string topic, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
        var created = OutboxMessage.Create(tenantId, clock.UtcNow, topic, json, Activity.Current?.Id);
        if (created.IsFailure) return Result.Failure(created.Error);

        db.OutboxMessages.Add(created.Value);   // saved later, together with the work, by the caller's SaveChanges
        return Result.Success();
    }
}
