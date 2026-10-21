using Microsoft.Extensions.Options;
using Serilog;
using VeilGate.Api.Middleware;
using VeilGate.Api.Options;
using VeilGate.Application;
using VeilGate.Infrastructure;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console(new Serilog.Formatting.Compact.CompactJsonFormatter()));

    // Options are validated at startup: the app refuses to start on bad configuration.
    builder.Services.AddOptions<GatewayOptions>().BindConfiguration(GatewayOptions.Section).ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddOptions<AppCorsOptions>().BindConfiguration(AppCorsOptions.Section).ValidateDataAnnotations()
        .Validate(o => o.AllowedOrigins.All(x => x != "*" && Uri.TryCreate(x, UriKind.Absolute, out _)), "Cors origins must be exact absolute URLs, no wildcard.")
        .ValidateOnStart();

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddControllers().AddJsonOptions(o =>
    {
        o.JsonSerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; // mass-assignment defense
        o.JsonSerializerOptions.MaxDepth = 32;
    });
    builder.Services.AddProblemDetails();
    builder.Services.AddHealthChecks()
        .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"]);

    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        var origins = builder.Configuration.GetSection(AppCorsOptions.Section).Get<AppCorsOptions>()?.AllowedOrigins ?? [];
        p.WithOrigins(origins).AllowCredentials().WithHeaders("Authorization", "Content-Type", "X-CSRF-Token", "Idempotency-Key", "If-Match")
         .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE");
    }));

    builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

    var app = builder.Build();

    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseSerilogRequestLogging(o => o.GetLevel = (_, _, ex) => ex is null ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error);
    app.UseCors();

    // Authentication, tenant resolution and authorization are added in milestone M1.
    app.MapControllers();
    app.MapHealthChecks("/health/live", new() { Predicate = r => r.Tags.Contains("live") });

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "VeilGate API terminated unexpectedly");
    throw;
}
finally { Log.CloseAndFlush(); }

public partial class Program { }
