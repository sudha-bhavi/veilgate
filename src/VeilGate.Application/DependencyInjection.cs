using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.AddSingleton<Transformation.ITransformer, Transformation.Transformer>();
        services.AddScoped<Transformation.IOutboundLeakGate, Transformation.OutboundLeakGate>();
        services.AddScoped<Transformation.IRehydrator, Transformation.Rehydrator>();
        services.AddScoped<Gateway.IGatewayPipeline, Gateway.GatewayPipeline>();
                services.AddSingleton<Policies.BuiltInPolicyResolver>();
        services.AddScoped<Policies.IPolicyResolver, Policies.StoredPolicyResolver>();

        services.AddScoped<Policies.PolicyAuditor>();
        services.AddScoped<Policies.CreatePolicyHandler>();
        services.AddScoped<Policies.SaveDraftHandler>();
        services.AddScoped<Policies.ValidatePolicyHandler>();
        services.AddScoped<Policies.SubmitPolicyHandler>();
        services.AddScoped<Policies.PublishPolicyHandler>();
        services.AddScoped<Policies.RejectPolicyHandler>();
        services.AddScoped<Policies.RollbackPolicyHandler>();
        services.AddScoped<Policies.ArchivePolicyHandler>();
        services.AddScoped<Policies.GetPolicyHandler>();
        services.AddScoped<Policies.ListPoliciesHandler>();
        services.AddScoped<Policies.ListPolicyVersionsHandler>();
        services.AddScoped<Policies.DiffPolicyHandler>();
        services.AddScoped<Policies.TestPolicyHandler>();

        services.AddScoped<ApiClients.ApiClientAuditor>();
        services.AddScoped<ApiClients.CreateApiClientHandler>();
        services.AddScoped<ApiClients.UpdateApiClientHandler>();
        services.AddScoped<ApiClients.RotateSecretHandler>();
        services.AddScoped<ApiClients.RevokeApiClientHandler>();
        services.AddScoped<ApiClients.GetApiClientHandler>();
        services.AddScoped<ApiClients.ListApiClientsHandler>();
        services.AddScoped<ApiClients.IssueClientTokenHandler>();

        services.AddScoped<LlmProviders.LlmProviderAuditor>();
        services.AddScoped<LlmProviders.CreateLlmProviderHandler>();
        services.AddScoped<LlmProviders.UpdateLlmProviderHandler>();
        services.AddScoped<LlmProviders.ReorderLlmProvidersHandler>();
        services.AddScoped<LlmProviders.DeleteLlmProviderHandler>();
        services.AddScoped<LlmProviders.TestLlmProviderHandler>();
        services.AddScoped<LlmProviders.GetLlmProviderHandler>();
        services.AddScoped<LlmProviders.ListLlmProvidersHandler>();
                services.AddScoped<Evaluation.EvaluationAuditor>();
        services.AddScoped<Evaluation.CreateSyntheticDatasetHandler>();
        services.AddScoped<Evaluation.ListDatasetsHandler>();
        services.AddScoped<Evaluation.GetDatasetHandler>();
        services.AddScoped<Evaluation.CreateEvaluationHandler>();
        services.AddScoped<Evaluation.ListEvaluationsHandler>();
        services.AddScoped<Evaluation.GetEvaluationHandler>();
        services.AddScoped<Evaluation.GetEvaluationLeaksHandler>();
        services.AddScoped<Evaluation.GetEvaluationReportHandler>();
        services.AddScoped<Evaluation.CompareEvaluationsHandler>();
                services.AddScoped<Auth.StepUpHandler>();

        services.AddScoped<Audit.ListAuditEventsHandler>();
        services.AddScoped<Audit.ListAuditActionsHandler>();
        services.AddScoped<Audit.VerifyAuditChainHandler>();
                services.AddEvaluationRunner();
        return services;
    }

    /// <summary>What an evaluation worker needs from the application layer, without the HTTP-facing handlers.</summary>
    public static IServiceCollection AddEvaluationRunner(this IServiceCollection services)
    {
        services.TryAddScoped<Policies.IPolicyEngine, Policies.PolicyEngine>();
        services.AddScoped<Evaluation.EvaluationRunner>();
        return services;
    }
}
