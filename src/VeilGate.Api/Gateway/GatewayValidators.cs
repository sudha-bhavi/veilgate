using FluentValidation;
using Microsoft.Extensions.Options;
using VeilGate.Api.Options;
using VeilGate.Application.Llm;

namespace VeilGate.Api.Gateway;

// Limits are from Backend doc 4.1. Messages never repeat what the caller sent: a rejected request may contain patient data.

public sealed class DetectRequestValidator : AbstractValidator<DetectRequest>
{
    public DetectRequestValidator()
    {
        RuleFor(x => x.Text).NotEmpty().WithMessage("Text is required")
            .MaximumLength(100_000).WithMessage("Maximum 100000 characters");
        RuleFor(x => x.Options!.MinConfidence).InclusiveBetween(0.0, 1.0).WithMessage("Must be between 0 and 1")
            .When(x => x.Options?.MinConfidence is not null);
    }
}

public sealed class DeidentifyRequestValidator : AbstractValidator<DeidentifyRequest>
{
    public DeidentifyRequestValidator(IOptions<GatewayOptions> gateway)
    {
        var maxTtl = gateway.Value.VaultTtlMinutesMax * 60;

        RuleFor(x => x.Text).NotEmpty().WithMessage("Text is required")
            .MaximumLength(100_000).WithMessage("Maximum 100000 characters");
        RuleFor(x => x.SubjectId).Matches("^[A-Za-z0-9._-]{1,64}$").WithMessage("1 to 64 characters from A-Z a-z 0-9 . _ -")
            .When(x => x.SubjectId is not null);
        RuleFor(x => x.VaultTtlSeconds).InclusiveBetween(60, maxTtl).WithMessage($"Must be between 60 and {maxTtl}")
            .When(x => x.VaultTtlSeconds is not null);
    }
}

public sealed class ReidentifyRequestValidator : AbstractValidator<ReidentifyRequest>
{
    public ReidentifyRequestValidator()
    {
        RuleFor(x => x.VaultSessionId).NotEmpty().WithMessage("Vault session id is required");
        RuleFor(x => x.Text).NotEmpty().WithMessage("Text is required")
            .MaximumLength(100_000).WithMessage("Maximum 100000 characters");
    }
}

public sealed class ChatCompletionRequestValidator : AbstractValidator<ChatCompletionRequest>
{
    private static readonly string[] Roles = ["system", "user", "assistant"];

    public ChatCompletionRequestValidator()
    {
        RuleFor(x => x.Model).NotEmpty().WithMessage("Model is required")
            .MaximumLength(80).WithMessage("Maximum 80 characters");

        RuleFor(x => x.Messages).NotNull().WithMessage("Messages are required")
            .Must(m => m is { Count: >= 1 and <= 50 }).WithMessage("Between 1 and 50 messages");
        RuleForEach(x => x.Messages).ChildRules(message =>
        {
            message.RuleFor(m => m.Role).Must(r => Roles.Contains(r)).WithMessage("Must be system, user or assistant");
            message.RuleFor(m => m.Content).NotNull().WithMessage("Content is required")
                .MaximumLength(32_768).WithMessage("Maximum 32768 characters");
        });

        RuleFor(x => x.Temperature).InclusiveBetween(0.0, 2.0).WithMessage("Must be between 0 and 2").When(x => x.Temperature is not null);
        RuleFor(x => x.MaxTokens).InclusiveBetween(1, 8192).WithMessage("Must be between 1 and 8192").When(x => x.MaxTokens is not null);
        RuleFor(x => x.Stream).Must(s => s != true).WithMessage("Streaming is not supported yet");
    }
}

/// <summary>
/// "Model must be one of the models of an enabled provider" (Backend doc 4.1), looked up for the tenant of this request. It is a
/// validator of its own because it needs the database, while the rules above need nothing and stay simple to test. The validation
/// filter runs both. It stays quiet when the model is missing or too long, since the other validator already says so.
/// </summary>
public sealed class ChatModelAvailabilityValidator : AbstractValidator<ChatCompletionRequest>
{
    public ChatModelAvailabilityValidator(ILlmProviderCatalog catalog)
    {
        RuleFor(x => x.Model)
            .MustAsync((model, ct) => catalog.ServesModelAsync(model, ct))
            .WithMessage("No provider serves this model")
            .When(x => !string.IsNullOrWhiteSpace(x.Model) && x.Model.Length <= 80);
    }
}

public sealed class PlaygroundRequestValidator : AbstractValidator<PlaygroundRequest>
{
    public PlaygroundRequestValidator()
    {
        RuleFor(x => x.Mode).Must(m => m is "detect" or "deidentify" or "chat").WithMessage("Must be detect, deidentify or chat");
        RuleFor(x => x.Text).NotEmpty().WithMessage("Text is required")
            .MaximumLength(32_768).WithMessage("Maximum 32768 characters");
        RuleFor(x => x.Model).NotEmpty().WithMessage("Model is required for chat").When(x => x.Mode == "chat");
    }
}
