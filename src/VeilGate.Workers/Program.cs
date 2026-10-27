using Serilog;
using VeilGate.Application;
using VeilGate.Infrastructure;
using VeilGate.Infrastructure.Evaluation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSerilog((_, cfg) => cfg.WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddEvaluationRunner();
builder.Services.AddSingleton<EvaluationQueueProcessor>();
builder.Services.Configure<EvaluationWorkerOptions>(builder.Configuration.GetSection("Evaluation"));
builder.Services.AddHostedService<HeartbeatWorker>();
builder.Services.AddHostedService<EvaluationWorker>();
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