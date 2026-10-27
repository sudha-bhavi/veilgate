using FluentAssertions;
using Microsoft.Extensions.Options;
using VeilGate.Api.Filters;
using VeilGate.Api.Gateway;
using VeilGate.Api.Options;
using VeilGate.Application.Llm;
using Xunit;

namespace VeilGate.Api.Tests;

public class GatewayValidatorTests
{
    private static readonly IOptions<GatewayOptions> Gateway = Microsoft.Extensions.Options.Options.Create(new GatewayOptions());

    /// <summary>A tenant whose providers serve exactly the given models.</summary>
    private sealed class FakeCatalog(params string[] models) : ILlmProviderCatalog
    {
        public Task<IReadOnlyList<LlmProviderConfig>> GetForModelAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderConfig>>([]);
        public Task<bool> ServesModelAsync(string model, CancellationToken ct = default) => Task.FromResult(models.Contains(model));
        public Task<LlmProviderConfig?> FindAsync(Guid providerId, CancellationToken ct = default) => Task.FromResult<LlmProviderConfig?>(null);
    }

    private static ChatCompletionRequest Chat(
        string model = "gpt-4o-mini", List<ChatMessageDto>? messages = null, bool? stream = null, double? temperature = null, int? maxTokens = null) =>
        new(model, messages ?? [new("user", "hello")], stream, temperature, maxTokens);

    private static List<string> Errors(FluentValidation.Results.ValidationResult r) => r.Errors.Select(e => $"{ValidationFilter.CamelCase(e.PropertyName)}: {e.ErrorMessage}").ToList();

    // -------------------------------------------------------------------- chat

    [Fact]
    public void A_normal_chat_request_is_valid() =>
        new ChatCompletionRequestValidator().Validate(Chat(temperature: 0.2, maxTokens: 512)).IsValid.Should().BeTrue();

    [Fact]
    public void An_oversized_message_names_the_exact_field()
    {
        var request = Chat(messages: [new("user", "ok"), new("user", new string('a', 32_769))]);

        var errors = Errors(new ChatCompletionRequestValidator().Validate(request));

        errors.Should().Equal("messages[1].content: Maximum 32768 characters");
    }

    [Fact]
    public void Too_many_or_no_messages_are_rejected()
    {
        var validator = new ChatCompletionRequestValidator();

        validator.Validate(Chat(messages: [])).IsValid.Should().BeFalse();
        validator.Validate(Chat(messages: Enumerable.Range(0, 51).Select(_ => new ChatMessageDto("user", "x")).ToList())).IsValid.Should().BeFalse();
        validator.Validate(Chat(messages: Enumerable.Range(0, 50).Select(_ => new ChatMessageDto("user", "x")).ToList())).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("tool")]
    [InlineData("admin")]
    [InlineData("")]
    public void Only_system_user_and_assistant_roles_are_accepted(string role) =>
        new ChatCompletionRequestValidator().Validate(Chat(messages: [new(role, "x")])).IsValid.Should().BeFalse();

    [Theory]
    [InlineData(-0.1)]
    [InlineData(2.1)]
    public void Temperature_must_be_between_0_and_2(double temperature) =>
        new ChatCompletionRequestValidator().Validate(Chat(temperature: temperature)).IsValid.Should().BeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    public void Max_tokens_must_be_between_1_and_8192(int maxTokens) =>
        new ChatCompletionRequestValidator().Validate(Chat(maxTokens: maxTokens)).IsValid.Should().BeFalse();

    [Fact]
    public async Task A_model_no_provider_serves_is_rejected()
    {
        var validator = new ChatModelAvailabilityValidator(new FakeCatalog("gpt-4o-mini"));

        (await validator.ValidateAsync(Chat(model: "gpt-4o-mini"))).IsValid.Should().BeTrue();
        Errors(await validator.ValidateAsync(Chat(model: "other"))).Should().Equal("model: No provider serves this model");
    }

    [Fact]
    public async Task The_availability_check_stays_quiet_about_a_missing_or_overlong_model_because_the_other_validator_reports_it()
    {
        var validator = new ChatModelAvailabilityValidator(new FakeCatalog());

        (await validator.ValidateAsync(Chat(model: ""))).IsValid.Should().BeTrue();
        (await validator.ValidateAsync(Chat(model: new string('m', 81)))).IsValid.Should().BeTrue();
        Errors(new ChatCompletionRequestValidator().Validate(Chat(model: ""))).Should().Contain("model: Model is required");
    }

    [Fact]
    public void Streaming_is_refused_until_it_exists() =>
        Errors(new ChatCompletionRequestValidator().Validate(Chat(stream: true))).Should().Equal("stream: Streaming is not supported yet");

    // -------------------------------------------------- detect / de-identify / re-identify

    [Fact]
    public void Detect_needs_text_within_the_limit()
    {
        var validator = new DetectRequestValidator();

        validator.Validate(new DetectRequest("hello", null)).IsValid.Should().BeTrue();
        validator.Validate(new DetectRequest("", null)).IsValid.Should().BeFalse();
        validator.Validate(new DetectRequest(new string('a', 100_001), null)).IsValid.Should().BeFalse();
        validator.Validate(new DetectRequest("x", new DetectOptionsDto(1.5))).IsValid.Should().BeFalse();
        validator.Validate(new DetectRequest("x", new DetectOptionsDto(0.5))).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData("patient-881", true)]
    [InlineData("a.b_c-9", true)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("", false)]
    public void Subject_id_allows_only_safe_characters(string subjectId, bool valid) =>
        new DeidentifyRequestValidator(Gateway).Validate(new DeidentifyRequest("x", null, subjectId, null)).IsValid.Should().Be(valid);

    [Fact]
    public void A_subject_id_over_64_characters_is_rejected() =>
        new DeidentifyRequestValidator(Gateway).Validate(new DeidentifyRequest("x", null, new string('a', 65), null)).IsValid.Should().BeFalse();

    [Theory]
    [InlineData(59, false)]
    [InlineData(60, true)]
    [InlineData(86_400, true)]
    [InlineData(86_401, false)]
    public void Vault_ttl_is_between_60_seconds_and_the_tenant_maximum(int seconds, bool valid) =>
        new DeidentifyRequestValidator(Gateway).Validate(new DeidentifyRequest("x", null, null, seconds)).IsValid.Should().Be(valid);

    [Fact]
    public void Reidentify_needs_a_session_and_text()
    {
        var validator = new ReidentifyRequestValidator();

        validator.Validate(new ReidentifyRequest(Guid.NewGuid(), "text")).IsValid.Should().BeTrue();
        validator.Validate(new ReidentifyRequest(Guid.Empty, "text")).IsValid.Should().BeFalse();
        validator.Validate(new ReidentifyRequest(Guid.NewGuid(), "")).IsValid.Should().BeFalse();
    }

    [Fact]
    public void Error_messages_never_repeat_what_the_caller_sent()
    {
        var secret = "Patient John Smith " + new string('a', 40_000);

        var errors = Errors(new ChatCompletionRequestValidator().Validate(Chat(messages: [new("user", secret)])));

        errors.Should().ContainSingle().Which.Should().NotContain("John Smith");
    }

    // ------------------------------------------------------------------- paths

    [Theory]
    [InlineData("Messages[1].Content", "messages[1].content")]
    [InlineData("Text", "text")]
    [InlineData("Options.MinConfidence", "options.minConfidence")]
    public void Field_paths_use_the_spelling_of_the_json(string input, string expected) =>
        ValidationFilter.CamelCase(input).Should().Be(expected);
}
