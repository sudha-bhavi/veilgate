using Microsoft.Extensions.Options;
using VeilGate.Infrastructure.Messaging;

/// <summary>Polls the outbox and sends what is due. Waits only when there was nothing to send.</summary>
public sealed class OutboxPublisherWorker(OutboxPublishProcessor processor, IOptions<OutboxPublisherOptions> options, ILogger<OutboxPublisherWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(options.Value.PollMilliseconds, 100, 10_000));
        log.LogInformation("outbox publisher started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await processor.PublishBatchAsync(stoppingToken) > 0) continue;   // more may be waiting
                await Task.Delay(poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // For example the database is down. Log the kind only, wait, and try again; claimed messages come back when their lease ends.
                log.LogError("outbox publisher error: {ErrorType}", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
