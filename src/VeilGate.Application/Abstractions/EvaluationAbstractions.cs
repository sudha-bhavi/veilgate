using VeilGate.Domain.Evaluation;

namespace VeilGate.Application.Abstractions;

/// <summary>A run a worker has just taken from the queue, and the tenant it belongs to.</summary>
public sealed record ClaimedRun(Guid RunId, Guid TenantId);

public interface IEvaluationRepository
{
    void AddDataset(EvalDataset dataset, IEnumerable<EvalCase> cases);
    Task<EvalDataset?> FindDatasetAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<EvalDataset>> ListDatasetsAsync(CancellationToken ct);
    Task<bool> DatasetNameExistsAsync(string name, CancellationToken ct);

    /// <summary>A page of notes in order. Pass -1 to start from the first.</summary>
    Task<IReadOnlyList<EvalCase>> GetCasesAsync(Guid datasetId, int afterOrdinal, int take, CancellationToken ct);

    void AddRun(EvalRun run);
    /// <summary>Tracked, so the caller can change it and save. Null if it is not in the current tenant.</summary>
    Task<EvalRun?> FindRunAsync(Guid id, CancellationToken ct);
    Task<IReadOnlyList<EvalRun>> ListRunsAsync(int skip, int take, CancellationToken ct);
    /// <summary>Runs waiting or running: the number a tenant is capped on.</summary>
    Task<int> CountActiveRunsAsync(CancellationToken ct);

    void AddLeaks(IEnumerable<EvalLeak> leaks);
    Task<IReadOnlyList<EvalLeak>> ListLeaksAsync(Guid runId, int skip, int take, CancellationToken ct);
        /// <summary>A run that is started again begins from nothing, so leaks from an abandoned attempt are removed first.</summary>
    Task DeleteLeaksAsync(Guid runId, CancellationToken ct);

    /// <summary>
    /// For the worker, with no tenant set. Atomically takes the oldest queued run (marking it running) and first recovers runs
    /// abandoned by a crashed worker. Null when nothing is waiting.
    /// </summary>
    Task<ClaimedRun?> ClaimNextRunAsync(TimeSpan staleAfter, int maxAttempts, CancellationToken ct);

    Task SaveChangesAsync(CancellationToken ct);
}