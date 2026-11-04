/// <summary>Runs one job again and again with a pause between runs. A failed run is logged (the kind of error only) and tried again after the pause.</summary>
public abstract class PeriodicWorker(string name, ILogger log) : BackgroundService
{
    protected abstract TimeSpan Interval { get; }

    protected abstract Task RunOnceAsync(CancellationToken ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(Interval.TotalSeconds, 1));
        log.LogInformation("{Worker} started, every {Seconds} seconds", name, (int)interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError("{Worker} error: {ErrorType}", name, ex.GetType().Name); }

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
