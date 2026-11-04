using VeilGate.Application.Abstractions;
using VeilGate.Domain.Messaging;
using VeilGate.Infrastructure.Messaging;

/// <summary>Reads the gateway topic and builds the request rows and hourly counters the Dashboard and Requests page show.</summary>
public sealed class MetricsAggregatorWorker(IEventConsumer consumer, GatewayEventConsumerHandler handler, ILogger<MetricsAggregatorWorker> log)
    : BackgroundService
{
    public const string Group = "vg-metrics-aggregator";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("metrics aggregator started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await consumer.ConsumeAsync(Group, [OutboxTopics.GatewayRequests], handler.HandleAsync, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // For example the broker could not be reached when starting. Log the kind only, wait, and join the group again.
                log.LogError("metrics aggregator error: {ErrorType}", ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
