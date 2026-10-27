using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Evaluation;
using VeilGate.Domain;

namespace VeilGate.Infrastructure.Evaluation;

/// <summary>
/// Takes one queued run from the queue and runs it. The claim happens with no tenant set (the one narrow SQL function that can see
/// across tenants); the run itself happens in a fresh scope fixed to that run's tenant, so row-level security applies as usual.
/// </summary>
public sealed class EvaluationQueueProcessor(IServiceScopeFactory scopes, ILogger<EvaluationQueueProcessor> logger)
{
    /// <summary>True if a run was taken (whatever its outcome), false if the queue was empty.</summary>
    public async Task<bool> ProcessNextAsync(TimeSpan staleAfter, int maxAttempts, CancellationToken ct)
    {
        ClaimedRun? claimed;
        await using (var claimScope = scopes.CreateAsyncScope())
            claimed = await claimScope.ServiceProvider.GetRequiredService<IEvaluationRepository>().ClaimNextRunAsync(staleAfter, maxAttempts, ct);
        if (claimed is null) return false;

        try
        {
            await using var runScope = scopes.CreateAsyncScope();
            runScope.ServiceProvider.GetRequiredService<ITenantContext>().Set(claimed.TenantId);
            await runScope.ServiceProvider.GetRequiredService<EvaluationRunner>().RunAsync(claimed.RunId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // shutting down: the run stays "running" and is picked up again once it goes stale
        }
        catch (Exception ex)
        {
            // The exception type only: a message could contain text from a note.
            logger.LogError("Evaluation run {RunId} crashed: {ErrorType}", claimed.RunId, ex.GetType().Name);
            await FailAsync(claimed, "evaluation.internal_error");
        }
        return true;
    }

    private async Task FailAsync(ClaimedRun claimed, string code)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();   // a fresh scope: the crashed one may hold a broken connection
            scope.ServiceProvider.GetRequiredService<ITenantContext>().Set(claimed.TenantId);
            var repo = scope.ServiceProvider.GetRequiredService<IEvaluationRepository>();
            var run = await repo.FindRunAsync(claimed.RunId, CancellationToken.None);
            if (run is null || run.IsFinished) return;

            run.Fail(code, scope.ServiceProvider.GetRequiredService<IClock>().UtcNow);
            await repo.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError("Could not mark run {RunId} failed: {ErrorType}", claimed.RunId, ex.GetType().Name);
        }
    }
}