using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace VeilGate.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly, includeInternalTypes: true);
        services.AddScoped<Auth.LoginHandler>();
                services.AddScoped<Auth.StartTotpEnrollmentHandler>();
        services.AddScoped<Auth.ConfirmTotpHandler>();
                services.AddScoped<Auth.VerifyMfaHandler>();
                        services.AddScoped<Auth.RefreshHandler>();
        services.AddScoped<Auth.LogoutHandler>();
        services.AddScoped<Detection.IDetectionPipeline, Detection.DetectionPipeline>();
        services.AddScoped<Policies.IPolicyEngine, Policies.PolicyEngine>();
        return services;
    }
}
