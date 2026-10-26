namespace VeilGate.Application.Abstractions;

/// <summary>
/// Ok = every checked event is in sequence, linked to the one before, and its hash matches its contents.
/// If not, BrokenAtSeq is the first bad event; ExpectedHash is what it should be and ActualHash is what is stored.
/// </summary>
public sealed record ChainVerification(bool Ok, long Verified, long? BrokenAtSeq, string? ExpectedHash, string? ActualHash);

public interface IAuditChainVerifier
{
    Task<ChainVerification> VerifyAsync(Guid tenantId, long? fromSeq = null, long? toSeq = null, CancellationToken ct = default);
}