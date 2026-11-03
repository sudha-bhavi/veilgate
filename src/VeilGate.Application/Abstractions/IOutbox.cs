using VeilGate.Domain.Common;

namespace VeilGate.Application.Abstractions;

public interface IOutbox
{
    /// <summary>
    /// Adds a message to the unit of work that is already open. It is NOT sent and NOT saved here: it is saved by the same
    /// SaveChanges as the work it belongs to, so the work and its message either both exist or neither does.
    /// The payload must never contain content, only what consumers need.
    /// </summary>
    Result Enqueue(Guid tenantId, string topic, object payload);
}
