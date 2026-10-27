using FluentAssertions;
using VeilGate.Domain.LlmProviders;
using Xunit;

namespace VeilGate.Domain.Tests;

public class LlmProviderTests
{
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly ProtectedApiKey Key = new([1, 2, 3], 1, "wxyz");

    private static LlmProvider NewProvider(string name = "openai", string url = "https://api.example.com/", string[]? models = null) =>
        LlmProvider.Create(Guid.CreateVersion7(), Tenant, name, url, models ?? ["gpt-4o-mini", "gpt-4o"], Key).Value;

    // ------------------------------------------------------------ create

    [Fact]
    public void Create_makes_an_enabled_provider_with_defaults()
    {
        var id = Guid.CreateVersion7();

        var p = LlmProvider.Create(id, Tenant, "  openai  ", "https://api.example.com/", ["gpt-4o-mini"], Key).Value;

        p.Id.Should().Be(id);
        p.TenantId.Should().Be(Tenant);
        p.Name.Should().Be("openai");
        p.BaseUrl.Should().Be("https://api.example.com");
        p.Enabled.Should().BeTrue();
        p.TimeoutSeconds.Should().Be(60);
        p.KeyLast4.Should().Be("wxyz");
        p.ApiKeyCipher.Should().Equal(1, 2, 3);
        p.DekVersion.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_needs_a_name(string name)
        => LlmProvider.Create(Guid.CreateVersion7(), Tenant, name, "https://a.example.com", ["m"], Key).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_refuses_a_name_over_80_characters()
        => LlmProvider.Create(Guid.CreateVersion7(), Tenant, new string('x', 81), "https://a.example.com", ["m"], Key).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData(0)]
    [InlineData(301)]
    public void Create_keeps_the_timeout_within_bounds(int seconds)
        => LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", ["m"], Key, timeoutSeconds: seconds).IsFailure.Should().BeTrue();

    [Fact]
    public void Create_needs_a_stored_key_and_ids()
    {
        LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", ["m"], new ProtectedApiKey([], 1, "wxyz")).IsFailure.Should().BeTrue();
        LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", ["m"], new ProtectedApiKey([1], 0, "wxyz")).IsFailure.Should().BeTrue();
        LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", ["m"], new ProtectedApiKey([1], 1, "abc")).IsFailure.Should().BeTrue();
        LlmProvider.Create(Guid.Empty, Tenant, "x", "https://a.example.com", ["m"], Key).IsFailure.Should().BeTrue();
        LlmProvider.Create(Guid.CreateVersion7(), Guid.Empty, "x", "https://a.example.com", ["m"], Key).IsFailure.Should().BeTrue();
    }

    // ------------------------------------------------------------ models

    [Fact]
    public void Models_are_required_trimmed_and_deduplicated()
    {
        LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", [], Key).IsFailure.Should().BeTrue();
        LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", null, Key).IsFailure.Should().BeTrue();

        var p = LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", [" gpt-4o ", "gpt-4o", "claude-3.5"], Key).Value;
        p.Models.Should().Equal("gpt-4o", "claude-3.5");
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("semi;colon")]
    [InlineData("")]
    [InlineData("-leading-dash")]
    [InlineData("<script>")]
    public void A_model_name_with_odd_characters_is_refused(string model)
        => LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", [model], Key).IsFailure.Should().BeTrue();

    [Theory]
    [InlineData("gpt-4o-mini")]
    [InlineData("anthropic/claude-3.5-sonnet")]
    [InlineData("llama3:8b")]
    [InlineData("model_v2.1")]
    public void Ordinary_model_names_are_accepted(string model)
        => LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", [model], Key).IsSuccess.Should().BeTrue();

    [Fact]
    public void Too_many_models_are_refused()
    {
        var many = Enumerable.Range(0, LlmProvider.MaxModels + 1).Select(i => $"model-{i}");

        LlmProvider.Create(Guid.CreateVersion7(), Tenant, "x", "https://a.example.com", many, Key).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Serves_matches_a_model_exactly()
    {
        var p = NewProvider(models: ["gpt-4o-mini"]);

        p.Serves("gpt-4o-mini").Should().BeTrue();
        p.Serves("GPT-4O-MINI").Should().BeFalse();
        p.Serves("gpt-4o").Should().BeFalse();
    }

    // ------------------------------------------------------------ addresses

    [Theory]
    [InlineData("https://api.example.com", "https://api.example.com")]
    [InlineData("https://API.Example.com/", "https://api.example.com")]
    [InlineData("https://api.example.com/openai/", "https://api.example.com/openai")]
    [InlineData("https://api.example.com:8443/v1", "https://api.example.com:8443/v1")]
    [InlineData("https://api.example.com:443", "https://api.example.com")]
    [InlineData("  http://localhost:8089  ", "http://localhost:8089")]
    public void An_address_is_stored_in_one_canonical_spelling(string input, string expected)
    {
        LlmProvider.TryNormalizeBaseUrl(input, out var normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("api.example.com")]                       // no scheme
    [InlineData("ftp://api.example.com")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@api.example.com")]     // credentials in the address
    [InlineData("https://api.example.com?key=abc")]       // query
    [InlineData("https://api.example.com/#frag")]
    [InlineData("https://")]
    public void An_unsafe_or_malformed_address_is_refused(string input)
        => LlmProvider.TryNormalizeBaseUrl(input, out _).Should().BeFalse();

    [Fact]
    public void An_address_over_300_characters_is_refused()
        => LlmProvider.TryNormalizeBaseUrl("https://a.example.com/" + new string('x', 300), out _).Should().BeFalse();

    // ------------------------------------------------------------ update

    [Fact]
    public void Update_changes_what_is_given_and_leaves_the_rest()
    {
        var p = NewProvider();

        p.Update("renamed", "https://other.example.com", ["gpt-4o"], timeoutSeconds: null, enabled: false, priority: null).IsSuccess.Should().BeTrue();

        p.Name.Should().Be("renamed");
        p.BaseUrl.Should().Be("https://other.example.com");
        p.Models.Should().Equal("gpt-4o");
        p.Enabled.Should().BeFalse();
        p.TimeoutSeconds.Should().Be(60);
    }

    [Fact]
    public void An_update_with_one_bad_field_changes_nothing()
    {
        var p = NewProvider();

        p.Update("renamed", "not a url", ["gpt-4o"], 10, false, 5).IsFailure.Should().BeTrue();

        p.Name.Should().Be("openai");
        p.BaseUrl.Should().Be("https://api.example.com");
        p.TimeoutSeconds.Should().Be(60);
        p.Enabled.Should().BeTrue();
    }

    [Fact]
    public void Replacing_the_key_swaps_ciphertext_version_and_last_four_together()
    {
        var p = NewProvider();

        p.ReplaceApiKey(new ProtectedApiKey([9, 9], 2, "ABCD")).IsSuccess.Should().BeTrue();

        p.ApiKeyCipher.Should().Equal(9, 9);
        p.DekVersion.Should().Be(2);
        p.KeyLast4.Should().Be("ABCD");
        p.ReplaceApiKey(new ProtectedApiKey([], 2, "ABCD")).IsFailure.Should().BeTrue();
        p.KeyLast4.Should().Be("ABCD");
    }

    [Fact]
    public void Priority_can_be_set()
    {
        var p = NewProvider();
        p.SetPriority(7);
        p.Priority.Should().Be(7);
    }

    [Theory]
    [InlineData("sk-abcdefgh1234", "1234")]
    [InlineData("abcd", "abcd")]
    [InlineData("ab", "**ab")]
    public void Only_the_last_four_characters_of_a_key_are_ever_kept_for_display(string key, string expected)
        => LlmProvider.Last4Of(key).Should().Be(expected);
}
