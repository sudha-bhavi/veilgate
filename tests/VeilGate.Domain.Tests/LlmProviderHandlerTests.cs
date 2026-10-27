using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using VeilGate.Application.Abstractions;
using VeilGate.Application.Llm;
using VeilGate.Application.LlmProviders;
using VeilGate.Domain.Audit;
using VeilGate.Domain.Common;
using VeilGate.Domain.LlmProviders;
using Xunit;

namespace VeilGate.Domain.Tests;

/// <summary>Connecting, changing, ordering, testing and removing providers, against in-memory stand-ins.</summary>
public class LlmProviderHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();
    private static readonly LlmProviderActor Admin = new(Guid.CreateVersion7(), "203.0.113.9");
    private const string Key = "sk-live-0123456789abcd";

    private readonly FakeRepository _repo = new();
    private readonly FakeProtector _protector = new();
    private readonly FakeUrlValidator _urls = new();
    private readonly ITenantContext _tenant = Substitute.For<ITenantContext>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly List<AuditEvent> _events = [];
    private readonly LlmProviderAuditor _auditor;

    public LlmProviderHandlerTests()
    {
        _tenant.TenantId.Returns(Tenant);
        _clock.UtcNow.Returns(Now);
        _audit.WriteAsync(Arg.Do<AuditEvent>(e => _events.Add(e)), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _auditor = new LlmProviderAuditor(_audit, _tenant, _clock, NullLogger<LlmProviderAuditor>.Instance);
    }

    private CreateLlmProviderHandler Create() => new(_repo, _protector, _urls, _tenant, _auditor);
    private UpdateLlmProviderHandler Update() => new(_repo, _protector, _urls, _tenant, _auditor);

    private static CreateLlmProviderCommand NewProvider(
        string name = "openai", string url = "https://api.example.com", string key = Key, string[]? models = null, int? priority = null) =>
        new(name, url, key, models ?? ["gpt-4o-mini"], null, priority, null, Admin);

    private async Task<LlmProviderView> Connected(string name = "openai", string url = "https://api.example.com", int? priority = null) =>
        (await Create().HandleAsync(NewProvider(name, url, priority: priority))).Value;

    // ------------------------------------------------------------ create

    [Fact]
    public async Task Create_stores_the_key_encrypted_and_bound_to_the_provider_and_shows_only_the_last_four()
    {
        var result = await Create().HandleAsync(NewProvider());

        result.IsSuccess.Should().BeTrue();
        var stored = _repo.All.Single();
        stored.ApiKeyCipher.Should().NotBeEquivalentTo(Encoding.UTF8.GetBytes(Key));
        stored.KeyLast4.Should().Be("abcd");
        stored.TenantId.Should().Be(Tenant);
        result.Value.KeyLast4.Should().Be("abcd");
        _protector.Contexts.Should().ContainSingle().Which.Should().Be(LlmProviderKeys.Context(Tenant, stored.Id));   // the key opens for this provider only
        JsonSerializer.Serialize(result.Value).Should().NotContain(Key).And.NotContain("0123456789");
        _repo.Saves.Should().Be(1);
    }

    [Fact]
    public async Task Create_is_audited_without_the_key()
    {
        await Create().HandleAsync(NewProvider());

        var e = _events.Should().ContainSingle().Subject;
        e.Action.Should().Be("llmprovider.create");
        e.ResourceType.Should().Be("llm_provider");
        e.Details["provider"].Should().Be("openai");
        e.Details.Values.Should().NotContain(v => v.Contains("sk-live"));
    }

    [Fact]
    public async Task The_first_provider_gets_priority_one_and_later_ones_go_to_the_end()
    {
        var a = await Connected("a");
        var b = await Connected("b");
        var c = await Connected("c");

        (a.Priority, b.Priority, c.Priority).Should().Be((1, 2, 3));
    }

    [Fact]
    public async Task A_priority_can_be_given_explicitly()
        => (await Connected("a", priority: 50)).Priority.Should().Be(50);

    [Fact]
    public async Task Create_refuses_an_address_the_url_check_rejects_and_never_encrypts_or_saves()
    {
        _urls.Result = Result.Failure(Error.Validation("provider.url_forbidden", "internal"));

        var result = await Create().HandleAsync(NewProvider(url: "https://10.0.0.5"));

        result.Error.Code.Should().Be("provider.url_forbidden");
        _protector.Contexts.Should().BeEmpty();
        _repo.All.Should().BeEmpty();
        _events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://api.example.com")]
    [InlineData("https://user:pw@api.example.com")]
    public async Task Create_refuses_a_malformed_address_before_any_lookup(string url)
    {
        var result = await Create().HandleAsync(NewProvider(url: url));

        result.Error.Code.Should().Be("validation.failed");
        _urls.Checked.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task Create_refuses_a_missing_or_too_short_key(string key)
        => (await Create().HandleAsync(NewProvider(key: key))).Error.Code.Should().Be("validation.failed");

    [Fact]
    public async Task Create_refuses_a_name_that_is_taken_in_the_tenant()
    {
        await Connected("openai");

        var result = await Create().HandleAsync(NewProvider("openai", "https://other.example.com"));

        result.Error.Code.Should().Be("resource.conflict");
        _repo.All.Should().HaveCount(1);
    }

    [Fact]
    public async Task Create_passes_on_the_domain_rules_for_models()
    {
        var result = await Create().HandleAsync(NewProvider(models: ["bad model name"]));

        result.Error.Code.Should().Be("validation.failed");
        _repo.All.Should().BeEmpty();
    }

    // ------------------------------------------------------------ update

    private UpdateLlmProviderCommand Edit(LlmProviderView p, string? name = null, string? url = null, string? key = null, string[]? models = null, bool? enabled = null) =>
        new(p.Id, name ?? p.Name, url ?? p.BaseUrl, key, models ?? p.Models, null, null, enabled, Admin);

    [Fact]
    public async Task Update_without_a_key_keeps_the_stored_key()
    {
        var p = await Connected();
        var before = _repo.All.Single().ApiKeyCipher.ToArray();

        var result = await Update().HandleAsync(Edit(p, models: ["gpt-4o"], enabled: false));

        result.IsSuccess.Should().BeTrue();
        var stored = _repo.All.Single();
        stored.ApiKeyCipher.Should().Equal(before);
        stored.KeyLast4.Should().Be("abcd");
        stored.Models.Should().Equal("gpt-4o");
        stored.Enabled.Should().BeFalse();
        _events.Last().Action.Should().Be("llmprovider.update");
    }

    [Fact]
    public async Task Changing_the_address_without_entering_the_key_again_is_refused_so_a_stored_key_cannot_be_redirected()
    {
        var p = await Connected();
        var contexts = _protector.Contexts.Count;

        var result = await Update().HandleAsync(Edit(p, url: "https://attacker.example.net"));

        result.Error.Code.Should().Be("provider.key_required");
        _repo.All.Single().BaseUrl.Should().Be("https://api.example.com");
        _urls.Checked.Should().NotContain("https://attacker.example.net");   // not even looked at
        _protector.Contexts.Should().HaveCount(contexts);
    }

    [Fact]
    public async Task Changing_the_address_together_with_the_key_replaces_both()
    {
        var p = await Connected();
        var before = _repo.All.Single().ApiKeyCipher.ToArray();

        var result = await Update().HandleAsync(Edit(p, url: "https://new.example.com/", key: "another-key-WXYZ"));

        result.IsSuccess.Should().BeTrue();
        var stored = _repo.All.Single();
        stored.BaseUrl.Should().Be("https://new.example.com");
        stored.KeyLast4.Should().Be("WXYZ");
        stored.ApiKeyCipher.Should().NotEqual(before);
        _urls.Checked.Should().Contain("https://new.example.com");
        _protector.Contexts.Last().Should().Be(LlmProviderKeys.Context(Tenant, p.Id));
    }

    [Fact]
    public async Task A_new_address_the_url_check_rejects_changes_nothing()
    {
        var p = await Connected();
        var before = _repo.All.Single().ApiKeyCipher.ToArray();
        _urls.Result = Result.Failure(Error.Validation("provider.url_forbidden", "internal"));

        var result = await Update().HandleAsync(Edit(p, url: "https://10.0.0.5", key: "another-key-WXYZ"));

        result.Error.Code.Should().Be("provider.url_forbidden");
        var stored = _repo.All.Single();
        stored.BaseUrl.Should().Be("https://api.example.com");
        stored.ApiKeyCipher.Should().Equal(before);
        stored.KeyLast4.Should().Be("abcd");
    }

    [Fact]
    public async Task The_same_address_is_not_looked_up_again()
    {
        var p = await Connected();
        _urls.Checked.Clear();

        await Update().HandleAsync(Edit(p, name: "renamed"));

        _urls.Checked.Should().BeEmpty();
    }

    [Fact]
    public async Task A_provider_can_keep_its_own_name_but_not_take_anothers()
    {
        var a = await Connected("a");
        await Connected("b");

        (await Update().HandleAsync(Edit(a, name: "a"))).IsSuccess.Should().BeTrue();
        (await Update().HandleAsync(Edit(a, name: "b"))).Error.Code.Should().Be("resource.conflict");
    }

    [Fact]
    public async Task An_update_that_breaks_a_domain_rule_changes_nothing()
    {
        var p = await Connected();

        var result = await Update().HandleAsync(Edit(p, models: ["bad model name"], key: "another-key-WXYZ"));

        result.IsFailure.Should().BeTrue();
        var stored = _repo.All.Single();
        stored.Models.Should().Equal("gpt-4o-mini");
        stored.KeyLast4.Should().Be("abcd");
    }

    [Fact]
    public async Task Updating_an_unknown_provider_is_not_found()
    {
        var ghost = new LlmProviderView(Guid.NewGuid(), "x", "https://x.example.com", "abcd", ["m"], 60, 1, true, Now);

        (await Update().HandleAsync(Edit(ghost))).Error.Code.Should().Be("resource.not_found");
    }

    // ------------------------------------------------------------ reorder

    [Fact]
    public async Task Reorder_sets_the_failover_order_from_the_list()
    {
        var a = await Connected("a");
        var b = await Connected("b");
        var c = await Connected("c");

        var result = await new ReorderLlmProvidersHandler(_repo, _auditor).HandleAsync(new ReorderLlmProvidersCommand([c.Id, a.Id, b.Id], Admin));

        result.IsSuccess.Should().BeTrue();
        _repo.All.OrderBy(p => p.Priority).Select(p => p.Name).Should().Equal("c", "a", "b");
        _repo.All.Select(p => p.Priority).Order().Should().Equal(1, 2, 3);
        _events.Last().Action.Should().Be("llmprovider.reorder");
    }

    [Fact]
    public async Task Reorder_must_name_every_provider_exactly_once()
    {
        var a = await Connected("a");
        var b = await Connected("b");
        var handler = new ReorderLlmProvidersHandler(_repo, _auditor);

        (await handler.HandleAsync(new ReorderLlmProvidersCommand([a.Id], Admin))).IsFailure.Should().BeTrue();               // one missing
        (await handler.HandleAsync(new ReorderLlmProvidersCommand([a.Id, a.Id], Admin))).IsFailure.Should().BeTrue();         // a twice, b missing
        (await handler.HandleAsync(new ReorderLlmProvidersCommand([a.Id, b.Id, Guid.NewGuid()], Admin))).IsFailure.Should().BeTrue();   // a stranger
        (await handler.HandleAsync(new ReorderLlmProvidersCommand([a.Id, Guid.NewGuid()], Admin))).IsFailure.Should().BeTrue();          // right count, wrong id

        _repo.All.OrderBy(p => p.Priority).Select(p => p.Name).Should().Equal("a", "b");   // nothing moved
    }

    // ------------------------------------------------------------ delete, get, list

    [Fact]
    public async Task Delete_removes_the_provider_and_audits_it()
    {
        var p = await Connected();

        var result = await new DeleteLlmProviderHandler(_repo, _auditor).HandleAsync(new DeleteLlmProviderCommand(p.Id, Admin));

        result.IsSuccess.Should().BeTrue();
        _repo.All.Should().BeEmpty();
        _events.Last().Action.Should().Be("llmprovider.delete");
    }

    [Fact]
    public async Task Deleting_an_unknown_provider_is_not_found()
        => (await new DeleteLlmProviderHandler(_repo, _auditor).HandleAsync(new DeleteLlmProviderCommand(Guid.NewGuid(), Admin)))
            .Error.Code.Should().Be("resource.not_found");

    [Fact]
    public async Task Listing_and_getting_never_expose_the_key()
    {
        var p = await Connected();

        var list = await new ListLlmProvidersHandler(_repo).HandleAsync();
        var one = await new GetLlmProviderHandler(_repo).HandleAsync(p.Id);

        JsonSerializer.Serialize(list).Should().NotContain("sk-live").And.NotContain("0123456789");
        JsonSerializer.Serialize(one.Value).Should().NotContain("sk-live").And.Contain("abcd");
        (await new GetLlmProviderHandler(_repo).HandleAsync(Guid.NewGuid())).Error.Code.Should().Be("resource.not_found");
    }

    // ------------------------------------------------------------ test button

    [Fact]
    public async Task Testing_calls_the_provider_with_its_decrypted_key_and_audits_the_outcome()
    {
        var p = await Connected();
        var catalog = new FakeCatalog(_repo, _protector);
        var tester = new FakeTester(new ProviderTestResult(true, 42, null));

        var result = await new TestLlmProviderHandler(catalog, tester, _auditor).HandleAsync(new TestLlmProviderCommand(p.Id, Admin));

        result.Value.Ok.Should().BeTrue();
        tester.Received!.ApiKey.Should().Be(Key);   // decrypted only for the call
        tester.Received.Id.Should().Be(p.Id);
        _events.Last().Action.Should().Be("llmprovider.test");
        _events.Last().Result.Should().Be("success");
    }

    [Fact]
    public async Task A_failed_test_is_audited_as_a_failure_with_its_category_only()
    {
        var p = await Connected();
        var tester = new FakeTester(new ProviderTestResult(false, 5, "timeout"));

        var result = await new TestLlmProviderHandler(new FakeCatalog(_repo, _protector), tester, _auditor)
            .HandleAsync(new TestLlmProviderCommand(p.Id, Admin));

        result.Value.ErrorCategory.Should().Be("timeout");
        _events.Last().Result.Should().Be("failure");
        _events.Last().Details["reason"].Should().Be("timeout");
        _events.Last().Details.Values.Should().NotContain(v => v.Contains("sk-live"));
    }

    [Fact]
    public async Task Testing_an_unknown_provider_is_not_found_and_calls_nothing()
    {
        var tester = new FakeTester(new ProviderTestResult(true, 1, null));

        var result = await new TestLlmProviderHandler(new FakeCatalog(_repo, _protector), tester, _auditor)
            .HandleAsync(new TestLlmProviderCommand(Guid.NewGuid(), Admin));

        result.Error.Code.Should().Be("resource.not_found");
        tester.Received.Should().BeNull();
    }

    // ------------------------------------------------------------ stand-ins

    private sealed class FakeRepository : ILlmProviderRepository
    {
        private readonly List<LlmProvider> _items = [];
        public IReadOnlyList<LlmProvider> All => _items;
        public int Saves;

        public void Add(LlmProvider provider) => _items.Add(provider);
        public void Remove(LlmProvider provider) => _items.Remove(provider);
        public Task<LlmProvider?> FindAsync(Guid id, CancellationToken ct) => Task.FromResult(_items.FirstOrDefault(p => p.Id == id));
        public Task<IReadOnlyList<LlmProvider>> ListAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LlmProvider>>(_items.OrderBy(p => p.Priority).ThenBy(p => p.Name).ToList());
        public Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken ct) =>
            Task.FromResult(_items.Any(p => p.Name == name && p.Id != exceptId));
        public Task SaveChangesAsync(CancellationToken ct) { Saves++; return Task.CompletedTask; }
    }

    /// <summary>Not real encryption: it just proves the context is passed through and the plaintext is not stored as it is.</summary>
    private sealed class FakeProtector : ITenantSecretProtector
    {
        public readonly List<string> Contexts = [];

        public Task<ProtectedSecret> ProtectAsync(Guid tenantId, string plaintext, string context, CancellationToken ct = default)
        {
            Contexts.Add(context);
            var cipher = Encoding.UTF8.GetBytes($"enc({context})").Concat(Encoding.UTF8.GetBytes(plaintext).Reverse()).ToArray();
            return Task.FromResult(new ProtectedSecret(cipher, 1));
        }

        public Task<string> UnprotectAsync(Guid tenantId, byte[] cipher, int dekVersion, string context, CancellationToken ct = default)
        {
            var prefix = Encoding.UTF8.GetBytes($"enc({context})");
            if (!cipher.Take(prefix.Length).SequenceEqual(prefix)) throw new System.Security.Cryptography.CryptographicException("wrong context");
            return Task.FromResult(Encoding.UTF8.GetString(cipher.Skip(prefix.Length).Reverse().ToArray()));
        }
    }

    private sealed class FakeUrlValidator : IOutboundUrlValidator
    {
        public Result Result = Result.Success();
        public readonly List<string> Checked = [];
        public Task<Result> ValidateAsync(string url, CancellationToken ct = default) { Checked.Add(url); return Task.FromResult(Result); }
    }

    private sealed class FakeCatalog(FakeRepository repo, FakeProtector protector) : ILlmProviderCatalog
    {
        public Task<IReadOnlyList<LlmProviderConfig>> GetForModelAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LlmProviderConfig>>([]);
        public Task<bool> ServesModelAsync(string model, CancellationToken ct = default) => Task.FromResult(false);

        public async Task<LlmProviderConfig?> FindAsync(Guid providerId, CancellationToken ct = default)
        {
            var p = repo.All.FirstOrDefault(x => x.Id == providerId);
            if (p is null) return null;
            var key = await protector.UnprotectAsync(p.TenantId, p.ApiKeyCipher, p.DekVersion, LlmProviderKeys.Context(p.TenantId, p.Id), ct);
            return new LlmProviderConfig(p.Id, p.TenantId, p.Name, p.BaseUrl, key, p.TimeoutSeconds, p.Priority, p.Models);
        }
    }

    private sealed class FakeTester(ProviderTestResult answer) : ILlmProviderTester
    {
        public LlmProviderConfig? Received;
        public Task<ProviderTestResult> TestAsync(LlmProviderConfig provider, CancellationToken ct = default) { Received = provider; return Task.FromResult(answer); }
    }
}
