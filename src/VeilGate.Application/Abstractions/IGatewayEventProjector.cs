namespace VeilGate.Application.Abstractions;

public enum ProjectionOutcome
{
    /// <summary>The message was new: the request row and the hourly counters were written.</summary>
    Applied,

    /// <summary>The message had already been handled, so nothing was written. Normal after a redelivery.</summary>
    Duplicate,

    /// <summary>The message is not a valid gateway event and never will be. The caller sends it to the dead-letter topic.</summary>
    Rejected,
}

/// <summary>Turns one published gateway event into database rows, exactly once however often the message is delivered.</summary>
public interface IGatewayEventProjector
{
    /// <summary>
    /// Everything happens in one transaction: the message id is recorded, the request row is added and the hour's counters are
    /// updated. If anything fails, nothing is kept and the exception is thrown, so the caller can try the same message again.
    /// </summary>
    Task<ProjectionOutcome> ProjectAsync(Guid messageId, string payloadJson, CancellationToken ct = default);
}
