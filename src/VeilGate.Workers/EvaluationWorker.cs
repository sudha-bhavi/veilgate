using Microsoft.Extensions.Options;
using VeilGate.Infrastructure.Evaluation;

public sealed class EvaluationWorkerOptions
{
    public int PollSeconds { get; set; } = 2;

    /// <summary>A run that has been "running" longer than this is assumed to belong to a crashed worker and is taken again.</summary>
    public int StaleAfterMinutes { get; set; } = 15;
    public int MaxAttempts { get; set; } = 3;
}

/// <summary>Polls the evaluation queue. (Kafka and the outbox arrive in M6; until then the table is the queue.)</summary>
public sealed class EvaluationWorker(EvaluationQueueProcessor processor, IOptions<EvaluationWorkerOptions> options, ILogger<EvaluationWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var poll = TimeSpan.FromSeconds(Math.Clamp(o.PollSeconds, 1, 60));
        var staleAfter = TimeSpan.FromMinutes(Math.Max(o.StaleAfterMinutes, 1));
        var maxAttempts = Math.Max(o.MaxAttempts, 1);
        log.LogInformation("evaluation worker started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await processor.ProcessNextAsync(staleAfter, maxAttempts, stoppingToken)) continue;   // more may be waiting
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // For example the database is down. Log the kind only, wait, and try again.
                log.LogError("evaluation worker error: {ErrorType}", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}