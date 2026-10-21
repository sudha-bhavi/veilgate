using Serilog;
using VeilGate.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((_, cfg) => cfg.WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));
builder.Services.AddHostedService<HeartbeatWorker>();
// Outbox publisher, metrics aggregator, vault purger etc. are added in later milestones (Backend doc 6.6).
builder.Build().Run();

public sealed class HeartbeatWorker(ILogger<HeartbeatWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            log.LogInformation("workers alive");
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
