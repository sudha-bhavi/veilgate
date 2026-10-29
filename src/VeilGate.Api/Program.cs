using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Serilog;
using VeilGate.Api.Middleware;
using VeilGate.Api.Options;
using VeilGate.Api.Security;
using VeilGate.Application;
using VeilGate.Application.Abstractions;
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

    builder.Services.AddOptions<AuthOptions>().BindConfiguration(AuthOptions.Section).ValidateDataAnnotations().ValidateOnStart();
    builder.Services.AddSingleton<ISigningKeyProvider, SigningKeyProvider>();
    builder.Services.AddSingleton<ITokenService, TokenService>();

        // Two token checks that differ only in audience and type: the normal access token, and the short "password done, MFA pending" token.
    static TokenValidationParameters ValidationFor(AuthOptions auth, ISigningKeyProvider keys, string audience, string tokenType) => new()
    {
        ValidateIssuer = true, ValidIssuer = auth.Issuer,
        ValidateAudience = true, ValidAudience = audience,
        ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.FromSeconds(30),
        RequireSignedTokens = true, ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],   // blocks "none" and HS256 key-confusion tricks
        ValidTypes = [tokenType],                              // each scheme accepts exactly one kind of token
        IssuerSigningKeyResolver = (_, _, kid, _) => keys.ValidationKeys.Where(k => k.KeyId == kid),
        NameClaimType = "sub", RoleClaimType = "role"
    };

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer()
        .AddJwtBearer(TokenService.MfaChallengeScheme, _ => { });

    builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
        .Configure<ISigningKeyProvider, IOptions<AuthOptions>>((jwt, keys, auth) =>
        {
            jwt.MapInboundClaims = false;   // keep claim names exactly as issued: sub, tid, role, ver
            jwt.SaveToken = false;
            jwt.RequireHttpsMetadata = false; // no discovery document is used; keys come from our own provider
            jwt.TokenValidationParameters = ValidationFor(auth.Value, keys, auth.Value.Audience, TokenService.AccessTokenType);
        });

    builder.Services.AddOptions<JwtBearerOptions>(TokenService.MfaChallengeScheme)
        .Configure<ISigningKeyProvider, IOptions<AuthOptions>>((jwt, keys, auth) =>
        {
            jwt.MapInboundClaims = false;
            jwt.SaveToken = false;
            jwt.RequireHttpsMetadata = false;
            jwt.TokenValidationParameters = ValidationFor(auth.Value, keys, TokenService.MfaAudience(auth.Value), TokenService.MfaTokenType);
        });
  
    builder.Services.AddAuthorization(o => o.AddVeilGatePolicies());

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = async (ctx, ct) =>
        {
            var res = ctx.HttpContext.Response;
            var wait = ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry) ? (int)Math.Ceiling(retry.TotalSeconds) : 60;
            res.Headers.RetryAfter = wait.ToString();
            var problem = new ProblemDetails { Status = 429, Title = "Too many attempts. Try again later." };
            problem.Extensions["code"] = "rate_limit.exceeded";
            await res.WriteAsJsonAsync(problem, options: null, contentType: "application/problem+json", cancellationToken: ct);
        };
        o.AddPolicy("login", http => RateLimitPartition.GetFixedWindowLimiter(
            http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = http.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.LoginRateLimitPerMinute,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
        // The gateway is limited per caller, not per IP: one busy client behind a shared proxy must not starve the others.
        // The limiter runs after authentication (see below) so the token's client id is known here.
        // An API client has its own limit (Database doc 2.5), carried in its token as "rlim"; anyone else gets the gateway-wide default.
        // The limit is part of the partition key, so when a client's limit is changed its next token starts a fresh window at the new limit.
        o.AddPolicy("gateway", http =>
        {
            var perClient = int.TryParse(http.User.FindFirst("rlim")?.Value, out var rlim) && rlim > 0 ? rlim : (int?)null;
            var limit = perClient ?? http.RequestServices.GetRequiredService<IOptions<GatewayOptions>>().Value.RateLimitPerClientPerMinute;
            var who = http.User.FindFirst("client_id")?.Value ?? http.User.FindFirst("sub")?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter($"{who}:{limit}", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = limit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            });
        });
    });

    builder.Services.AddApplication();
    builder.Services.AddInfrastructure(builder.Configuration);

    builder.Services.AddControllers(o => o.Filters.Add<VeilGate.Api.Filters.ValidationFilter>())
        .AddJsonOptions(o =>
        {
            o.JsonSerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow; // mass-assignment defense
            o.JsonSerializerOptions.MaxDepth = 32;
        })
        .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = _ =>
        {
            // Broken JSON, an unknown property, a wrong type: 400 with a stable code. The details are not echoed back, since they could contain request data.
            var problem = new ProblemDetails { Status = 400, Title = "The request body is malformed." };
            problem.Extensions["code"] = "request.malformed";
            return new ObjectResult(problem) { StatusCode = 400, ContentTypes = { "application/problem+json" } };
        });
    builder.Services.AddValidatorsFromAssemblyContaining<VeilGate.Api.Filters.ValidationFilter>();
    builder.Services.AddProblemDetails();
    builder.Services.AddHealthChecks()
        .AddCheck("self", () => Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy(), tags: ["live"]);

    builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    {
        var origins = builder.Configuration.GetSection(AppCorsOptions.Section).Get<AppCorsOptions>()?.AllowedOrigins ?? [];
        p.WithOrigins(origins).AllowCredentials().WithHeaders("Authorization", "Content-Type", "X-CSRF-Token", "Idempotency-Key", "If-Match")
         .WithMethods("GET", "POST", "PUT", "PATCH", "DELETE");
    }));

    builder.Services.Configure<RouteOptions>(o => o.SetParameterPolicy<ConsolePathConstraint>(ConsolePathConstraint.Name));

    builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

    var app = builder.Build();

    app.UseMiddleware<ExceptionHandlingMiddleware>();
    app.UseMiddleware<SecurityHeadersMiddleware>();
    app.UseStaticFiles();   // the built console in wwwroot (the Docker image puts it there); its files are public, so before authentication
    app.UseSerilogRequestLogging(o => o.GetLevel = (_, _, ex) => ex is null ? Serilog.Events.LogEventLevel.Information : Serilog.Events.LogEventLevel.Error);
    app.UseCors();
    app.UseAuthentication();                       // 1. who is calling? (verifies the token signature, issuer, audience, expiry)
    app.UseRateLimiter();                          //    after authentication, so limits can be per client; login limits are still per IP
    app.UseMiddleware<TenantResolutionMiddleware>(); // 2. which tenant? only from the signed token, then check the token is still valid
    app.UseAuthorization();                        // 3. may they do this? (controllers require a token unless [AllowAnonymous])

    // Secure by default: an endpoint with no [Authorize] or [AllowAnonymous] of its own gets a plain [Authorize] (a signed-in person).
    // This is a convention rather than RequireAuthorization() because that would be stacked on top of the gateway's own policies,
    // and a client token could then never pass. Unknown URLs still get a plain 404, so signed-out callers learn nothing.
    app.MapControllers().Add(endpoint =>
    {
        if (!endpoint.Metadata.Any(m => m is Microsoft.AspNetCore.Authorization.IAuthorizeData or Microsoft.AspNetCore.Authorization.IAllowAnonymous))
            endpoint.Metadata.Add(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute());
    });
    app.MapHealthChecks("/health/live", new() { Predicate = r => r.Tags.Contains("live") }).AllowAnonymous();

    // The console's pages: any other path without a file extension gets index.html and the browser app takes over.
    // API paths are left out, so unknown API URLs stay a 404. In development there is no wwwroot (Vite serves the console),
    // and this simply finds no file.
    app.MapFallbackToFile($"{{*path:{ConsolePathConstraint.Name}:nonfile}}", "index.html");

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "VeilGate API terminated unexpectedly");
    throw;
}
finally { Log.CloseAndFlush(); }

public partial class Program { }
